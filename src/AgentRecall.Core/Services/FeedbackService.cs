using System.Text;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Memory;

namespace AgentRecall.Core.Services;

/// <summary>
/// Default <see cref="IFeedbackService"/>: records the raw feedback as a
/// <see cref="RecallEvent"/> and persists the extracted <see cref="RecallRule"/>
/// in <see cref="RuleStatus.Pending"/> state, linking the event to the rule.
///
/// Capture is deduplicated: when an equivalent rule already exists (same
/// guidance and scope, not retired), the feedback is recorded against that
/// rule instead of creating a duplicate. Every caller (CLI and MCP) goes
/// through here, so the behaviour is consistent everywhere.
/// </summary>
public sealed class FeedbackService : IFeedbackService
{
    // Statuses that should not be reused as a dedup target; a new rule is
    // created instead so retired guidance is not silently revived.
    private static readonly HashSet<RuleStatus> NotReusable =
        [RuleStatus.Superseded, RuleStatus.Archived, RuleStatus.Retired];

    /// <summary>Default confidence for a reusable engineering lesson.</summary>
    public const double EngineeringLessonConfidence = 0.7;

    /// <summary>Default confidence for a repository convention (lower than a lesson).</summary>
    public const double RepositoryConventionConfidence = 0.55;

    private readonly IRecallEventRepository _events;
    private readonly IRecallRuleRepository _rules;
    private readonly IRecallExtractor _extractor;
    private readonly IMemoryWorthinessClassifier _classifier;
    private readonly ICaptureDecisionPolicy _decisionPolicy;
    private readonly IAdaptiveWorthinessPolicy _adaptivePolicy;
    private readonly AgentRecallOptions _options;

    public FeedbackService(
        IRecallEventRepository events,
        IRecallRuleRepository rules,
        IRecallExtractor extractor,
        IMemoryWorthinessClassifier classifier,
        ICaptureDecisionPolicy decisionPolicy,
        IAdaptiveWorthinessPolicy adaptivePolicy,
        AgentRecallOptions options)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _decisionPolicy = decisionPolicy ?? throw new ArgumentNullException(nameof(decisionPolicy));
        _adaptivePolicy = adaptivePolicy ?? throw new ArgumentNullException(nameof(adaptivePolicy));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<FeedbackResult> AddAsync(FeedbackInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Screen the candidate against the "lessons, not facts" policy. A low-value
        // code fact is not a reusable lesson; a code fact that hints at a reusable
        // pattern is rewritten to the generalized lesson rather than the raw fact.
        MemoryWorthinessResult? worthiness = null;
        if (_options.MemoryWorthinessEnabled)
        {
            worthiness = _classifier.Classify(input.Feedback);
        }

        // Extract the candidate rule up front so a code fact still produces a comparable
        // rule for deduplication, and so the raw event can link to whatever is stored.
        var rule = _extractor.Extract(input);

        // For a NeedsReview verdict, store the generalized lesson, never the raw fact.
        if (worthiness is { Verdict: MemoryWorthiness.NeedsReview, SuggestedGeneralizedLesson: { Length: > 0 } lesson })
        {
            rule.RuleText = lesson;
        }

        // When an observed failure (or correction) elevated a generic observation, rewrite
        // it into conditional, branch-preserving form so it generalizes without overreaching
        // ("Always merge nested ifs" → "When flattening nested template conditionals, preserve
        // `{{else}}` semantics …"). Done before dedup so the conditional form is what compares.
        if (input.Context is { HasOutcomeEvidence: true } &&
            ConditionalLessonRewriter.Rewrite(input.Feedback) is { } conditional)
        {
            rule.Trigger = conditional.Trigger;
            rule.RuleText = conditional.RuleText;
            rule.Mistake = conditional.Mistake;
        }

        // Record the classified category and let it set the default trust: an
        // engineering lesson survives refactors, so it is trusted more than a
        // repository convention that names specific symbols.
        rule.Category = worthiness?.Category ?? RuleCategory.Unknown;
        ApplyCategoryConfidence(rule);

        // Deduplicate before deciding, so the decision knows whether this is new
        // knowledge or a repeat of something already captured.
        var existing = await FindEquivalentAsync(rule, cancellationToken).ConfigureAwait(false);

        // The deterministic final step: the single place that weighs worthiness,
        // confidence, the acceptance signal, duplicate detection and scope, and decides
        // whether to auto-capture, suggest, or skip — so the user is not asked to.
        var decision = _decisionPolicy.Decide(new CaptureSignals
        {
            Worthy = worthiness is null || worthiness.Verdict != MemoryWorthiness.NotWorthStoring,
            Confidence = worthiness?.Confidence ?? rule.Confidence,
            // An explicit accept (accepted PR comment, "apply the review", approve=true)
            // is the strongest signal; the configured default is only a posture.
            ExplicitAcceptance = input.AutoApprove == true,
            ApprovePosture = input.AutoApprove ?? _options.AutoApproveFeedback,
            IsDuplicate = existing is not null,
            CodeFactOverrideAllowed = _options.AllowCodeFactsWhenAccepted,
            ScopeLevel = rule.ScopeLevel,
            ScopeValue = rule.ScopeValue,
            WorthinessReason = worthiness?.Reason ?? "Memory-worthiness screening disabled.",
        });

        // Outcome-aware adjustment. Only applied when the caller supplied a context, so
        // every existing path (manual CLI/MCP feedback with no context) is unchanged. The
        // adaptive policy never re-derives signals; it only raises or lowers the decision.
        var captureReason = CaptureReason.None;
        string? evidenceSummary = null;
        if (input.Context is { } context)
        {
            var adaptive = _adaptivePolicy.Adjust(
                worthiness, context, decision, isDuplicate: existing is not null, conflictExists: context.ConflictExists);

            decision = decision with
            {
                Outcome = adaptive.Outcome,
                Confidence = adaptive.Confidence,
                // For a skip, the explanation is the user-facing reason ("Generic best
                // practice with no observed failure"); for a capture, keep the worthiness
                // rationale as the reason and surface the explanation as the notice.
                Reason = adaptive.Outcome == CaptureOutcome.Skip ? adaptive.Explanation : decision.Reason,
                Notice = adaptive.Explanation,
            };
            captureReason = adaptive.Reason;
            evidenceSummary = string.IsNullOrWhiteSpace(context.EvidenceSummary)
                ? null
                : context.EvidenceSummary!.Trim();
        }

        if (decision.Outcome == CaptureOutcome.Skip)
        {
            // A duplicate is reinforced (event recorded against the existing rule); a
            // non-duplicate skip (a code fact) stores nothing actionable.
            return existing is not null
                ? await ReinforceAsync(input, existing, worthiness, decision, captureReason, evidenceSummary, cancellationToken).ConfigureAwait(false)
                : await RejectAsync(input, worthiness, decision, captureReason, evidenceSummary, cancellationToken).ConfigureAwait(false);
        }

        // AutoCapture writes the rule live (Active); SuggestCapture parks it (Pending)
        // for the user to confirm. Either way it is stored inside AgentRecall.
        rule.Status = decision.Outcome == CaptureOutcome.AutoCapture ? RuleStatus.Active : RuleStatus.Pending;
        rule.CaptureReason = captureReason;
        rule.EvidenceSummary = evidenceSummary ?? string.Empty;
        // The adjusted confidence (raised by repeats/evidence) is the rule's confidence.
        if (input.Context is not null && !string.IsNullOrWhiteSpace(rule.RuleText) && !string.IsNullOrWhiteSpace(rule.Trigger))
        {
            rule.Confidence = decision.Confidence;
        }

        rule = await _rules.AddAsync(rule, cancellationToken).ConfigureAwait(false);

        var recallEvent = await _events.AddAsync(new RecallEvent
        {
            Type = RecallEventType.MistakeObserved,
            RuleId = rule.Id,
            Trigger = input.Task,
            Details = BuildDetails(input, captureReason, evidenceSummary),
        }, cancellationToken).ConfigureAwait(false);

        return new FeedbackResult(recallEvent, rule)
        {
            ReusedExistingRule = false,
            Worthiness = worthiness,
            Decision = decision,
            CaptureReason = captureReason,
            EvidenceSummary = evidenceSummary,
        };
    }

    /// <summary>
    /// Handles a duplicate (a Skip whose decision found an equivalent rule): records a
    /// fresh event against the existing rule so the repeat is still observed, but stores
    /// no new rule.
    /// </summary>
    private async Task<FeedbackResult> ReinforceAsync(
        FeedbackInput input,
        RecallRule existing,
        MemoryWorthinessResult? worthiness,
        CaptureDecision decision,
        CaptureReason captureReason,
        string? evidenceSummary,
        CancellationToken cancellationToken)
    {
        var recallEvent = await _events.AddAsync(new RecallEvent
        {
            Type = RecallEventType.MistakeObserved,
            RuleId = existing.Id,
            Trigger = input.Task,
            Details = BuildDetails(input, captureReason, evidenceSummary),
        }, cancellationToken).ConfigureAwait(false);

        // A repeat reinforces the existing rule; record the reason on it if it had none,
        // so a rule first stored on weak text gains the evidence the repeat provided.
        if (captureReason != CaptureReason.None && existing.CaptureReason == CaptureReason.None)
        {
            existing.CaptureReason = captureReason;
            if (string.IsNullOrWhiteSpace(existing.EvidenceSummary) && !string.IsNullOrWhiteSpace(evidenceSummary))
            {
                existing.EvidenceSummary = evidenceSummary!.Trim();
            }

            existing = await _rules.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        return new FeedbackResult(recallEvent, existing)
        {
            ReusedExistingRule = true,
            Worthiness = worthiness,
            Decision = decision,
            CaptureReason = captureReason,
            EvidenceSummary = evidenceSummary,
        };
    }

    /// <summary>
    /// Handles a non-duplicate Skip (a low-value code fact): stores no rule and, only
    /// when <see cref="AgentRecallOptions.StoreRejectedCandidates"/> is set, records an
    /// audit event linked to no rule.
    /// </summary>
    private async Task<FeedbackResult> RejectAsync(
        FeedbackInput input,
        MemoryWorthinessResult? worthiness,
        CaptureDecision decision,
        CaptureReason captureReason,
        string? evidenceSummary,
        CancellationToken cancellationToken)
    {
        RecallEvent? recallEvent = null;
        if (_options.StoreRejectedCandidates)
        {
            recallEvent = await _events.AddAsync(new RecallEvent
            {
                Type = RecallEventType.RuleRejected,
                RuleId = null,
                Trigger = input.Task,
                Details = $"Rejected as not memory-worthy: {decision.Reason}{Environment.NewLine}{BuildDetails(input, captureReason, evidenceSummary)}",
            }, cancellationToken).ConfigureAwait(false);
        }

        return new FeedbackResult(recallEvent, null)
        {
            Worthiness = worthiness,
            Decision = decision,
            CaptureReason = captureReason,
            EvidenceSummary = evidenceSummary,
        };
    }

    /// <summary>
    /// Sets the rule's confidence from its category, but only when the rule is
    /// structurally sound (the quality validator leaves a core-field-missing rule
    /// at a low ceiling, which must not be raised).
    /// </summary>
    private static void ApplyCategoryConfidence(RecallRule rule)
    {
        var sound = !string.IsNullOrWhiteSpace(rule.RuleText) && !string.IsNullOrWhiteSpace(rule.Trigger);
        if (!sound)
        {
            return;
        }

        rule.Confidence = rule.Category switch
        {
            RuleCategory.EngineeringLesson => EngineeringLessonConfidence,
            RuleCategory.RepositoryConvention => RepositoryConventionConfidence,
            _ => rule.Confidence,
        };
    }

    private async Task<RecallRule?> FindEquivalentAsync(RecallRule candidate, CancellationToken cancellationToken)
    {
        var key = NormalizeGuidance(candidate.RuleText);
        if (key.Length == 0)
        {
            return null;
        }

        var all = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(r =>
            !NotReusable.Contains(r.Status) &&
            r.ScopeLevel == candidate.ScopeLevel &&
            string.Equals(r.ScopeValue ?? string.Empty, candidate.ScopeValue ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            NormalizeGuidance(r.RuleText) == key);
    }

    /// <summary>
    /// Normalizes guidance for equivalence: lowercased, whitespace collapsed,
    /// and trailing punctuation removed, so trivially different phrasings of the
    /// same rule compare equal.
    /// </summary>
    private static string NormalizeGuidance(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words).ToLowerInvariant().TrimEnd('.', '!', '?', ' ');
    }

    private static string BuildDetails(FeedbackInput input, CaptureReason captureReason, string? evidenceSummary)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Feedback: {input.Feedback}");

        if (!string.IsNullOrWhiteSpace(input.BadOutput))
        {
            sb.AppendLine($"Bad output: {input.BadOutput}");
        }

        if (!string.IsNullOrWhiteSpace(input.FixedOutput))
        {
            sb.AppendLine($"Fixed output: {input.FixedOutput}");
        }

        if (!string.IsNullOrWhiteSpace(input.Tags))
        {
            sb.AppendLine($"Tags: {input.Tags}");
        }

        if (captureReason != CaptureReason.None)
        {
            sb.AppendLine($"Capture reason: {captureReason}");
        }

        if (!string.IsNullOrWhiteSpace(evidenceSummary))
        {
            sb.AppendLine($"Evidence: {evidenceSummary}");
        }

        return sb.ToString().TrimEnd();
    }
}
