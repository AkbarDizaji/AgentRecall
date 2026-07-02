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
    // Statuses that should not be reused as a dedup target; a new rule is created instead
    // so retired guidance is not silently revived. Shares the central dead-status set.
    private static readonly IReadOnlyCollection<RuleStatus> NotReusable = RuleStatusSets.Inactive;

    /// <summary>Default confidence for a reusable engineering lesson.</summary>
    public const double EngineeringLessonConfidence = 0.7;

    /// <summary>Default confidence for a repository convention (lower than a lesson).</summary>
    public const double RepositoryConventionConfidence = 0.55;

    /// <summary>Default confidence for an explicitly stated user preference (high; the user's own word).</summary>
    public const double UserPreferenceConfidence = MemoryWorthinessClassifier.UserPreferenceConfidence;

    private readonly IRecallEventRepository _events;
    private readonly IRecallRuleRepository _rules;
    private readonly IRecallExtractor _extractor;
    private readonly IMemoryWorthinessClassifier _classifier;
    private readonly ICaptureDecisionPolicy _decisionPolicy;
    private readonly IAdaptiveWorthinessPolicy _adaptivePolicy;
    private readonly IRuleLifecycleRecommendationRepository _recommendations;
    private readonly AgentRecallOptions _options;

    public FeedbackService(
        IRecallEventRepository events,
        IRecallRuleRepository rules,
        IRecallExtractor extractor,
        IMemoryWorthinessClassifier classifier,
        ICaptureDecisionPolicy decisionPolicy,
        IAdaptiveWorthinessPolicy adaptivePolicy,
        IRuleLifecycleRecommendationRepository recommendations,
        AgentRecallOptions options)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _decisionPolicy = decisionPolicy ?? throw new ArgumentNullException(nameof(decisionPolicy));
        _adaptivePolicy = adaptivePolicy ?? throw new ArgumentNullException(nameof(adaptivePolicy));
        _recommendations = recommendations ?? throw new ArgumentNullException(nameof(recommendations));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<FeedbackResult> AddAsync(FeedbackInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);

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

        // An explicitly stated user preference is normalized into durable, bounded
        // conditional form (never the raw "always caveman" phrasing) and stored as a
        // user/communication preference — not a repository convention. It applies to the
        // user everywhere, so any repository-narrow scope is corrected to Global.
        var preference = worthiness is { CaptureReason: CaptureReason.ExplicitUserPreference };
        if (preference)
        {
            if (worthiness!.SuggestedGeneralizedLesson is { Length: > 0 } normalized)
            {
                rule.RuleText = normalized;
            }

            if (!string.IsNullOrWhiteSpace(worthiness.NormalizedTrigger))
            {
                rule.Trigger = worthiness.NormalizedTrigger!;
            }

            if (!string.IsNullOrWhiteSpace(worthiness.Tags))
            {
                rule.Tags = MergeTags(rule.Tags, worthiness.Tags!);
            }

            if (rule.ScopeLevel is ScopeLevel.Repository or ScopeLevel.Directory or ScopeLevel.File)
            {
                rule.ScopeLevel = ScopeLevel.Global;
                rule.ScopeValue = string.Empty;
            }
        }

        // Record the classified category and let it set the default trust: an
        // engineering lesson survives refactors, so it is trusted more than a
        // repository convention that names specific symbols; an explicit user
        // preference is trusted highly because it is the user's own word.
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
            IsExplicitUserPreference = preference,
            ScopeLevel = rule.ScopeLevel,
            ScopeValue = rule.ScopeValue,
            WorthinessReason = worthiness?.Reason ?? "Memory-worthiness screening disabled.",
        });

        // An explicit user preference carries its reason and evidence from the classifier
        // (the text alone is enough), so it is recorded even on the manual path with no
        // outcome context. A supplied context (turn finalizer) overrides below.
        var captureReason = worthiness?.CaptureReason ?? CaptureReason.None;
        string? evidenceSummary = preference && !string.IsNullOrWhiteSpace(worthiness!.EvidenceSummary)
            ? worthiness.EvidenceSummary!.Trim()
            : null;

        // Outcome-aware adjustment. Only applied when the caller supplied a context, so
        // every existing path (manual CLI/MCP feedback with no context) is unchanged. The
        // adaptive policy never re-derives signals; it only raises or lowers the decision.
        // An explicit user preference is exempt: it is captured on the user's word, so the
        // outcome-evidence heuristics (which would otherwise skip "generic advice") never
        // downgrade it — its reason and evidence already came from the classifier.
        if (input.Context is { } context && !preference)
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

        // A newer explicit preference about the same dimension (e.g. answer length) as an
        // older active one conflicts with it. Silently keeping both active would leave the
        // agent with contradictory guidance, so — rather than auto-superseding, which is
        // risky — record a Supersede recommendation the user can apply.
        if (preference && rule.Status == RuleStatus.Active)
        {
            await RecommendSupersedeConflictingPreferencesAsync(rule, worthiness!, cancellationToken).ConfigureAwait(false);
        }

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
    /// Validates the feedback intake: feedback text is required (non-empty after trim), and
    /// both feedback and task stay within sane length caps. The task may be empty (some
    /// callers, e.g. the MCP capture tool, supply only feedback).
    /// </summary>
    private void ValidateInput(FeedbackInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Feedback))
        {
            throw new ArgumentException("Feedback text is required and cannot be empty.", nameof(input));
        }

        if (input.Feedback.Length > _options.FeedbackMaxLength)
        {
            throw new ArgumentException(
                $"Feedback is {input.Feedback.Length} characters, which exceeds the maximum of {_options.FeedbackMaxLength}.",
                nameof(input));
        }

        if ((input.Task?.Length ?? 0) > _options.FeedbackMaxTaskLength)
        {
            throw new ArgumentException(
                $"Task is {input.Task!.Length} characters, which exceeds the maximum of {_options.FeedbackMaxTaskLength}.",
                nameof(input));
        }
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
            RuleCategory.UserPreference or RuleCategory.CommunicationPreference => UserPreferenceConfidence,
            _ => rule.Confidence,
        };
    }

    // The communication/interaction dimensions a preference can be about. Two preferences
    // about the same dimension (e.g. both about answer length) are treated as conflicting.
    private static readonly string[] PreferenceDimensionTags =
        ["verbosity", "language", "prompt-format", "questioning", "honesty", "explanation-level"];

    /// <summary>
    /// Merges the extractor's tags with the classifier-assigned preference tags,
    /// de-duplicated and order-preserving, into a single comma-separated string.
    /// </summary>
    private static string MergeTags(string? existing, string added)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();
        foreach (var tag in $"{existing},{added}".Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(tag))
            {
                merged.Add(tag);
            }
        }

        return string.Join(",", merged);
    }

    /// <summary>
    /// Records a Supersede recommendation for each in-force preference rule that governs
    /// the same dimension as the newly captured one but prescribes different guidance, so
    /// the user can retire the stale preference deliberately. Never mutates the old rule.
    /// </summary>
    private async Task RecommendSupersedeConflictingPreferencesAsync(
        RecallRule newer, MemoryWorthinessResult worthiness, CancellationToken cancellationToken)
    {
        var dimension = PreferenceDimensionTags.FirstOrDefault(d =>
            (worthiness.Tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(t => string.Equals(t, d, StringComparison.OrdinalIgnoreCase)));
        if (dimension is null)
        {
            return;
        }

        var newerKey = NormalizeGuidance(newer.RuleText);
        var candidates = await _rules.QueryAsync(new RuleQuery
        {
            ScopeLevel = newer.ScopeLevel,
            ScopeValue = newer.ScopeValue ?? string.Empty,
            ExcludeStatuses = NotReusable,
        }, cancellationToken).ConfigureAwait(false);

        foreach (var older in candidates)
        {
            if (older.Id == newer.Id ||
                older.Category is not (RuleCategory.CommunicationPreference or RuleCategory.UserPreference) ||
                NormalizeGuidance(older.RuleText) == newerKey ||
                !HasDimensionTag(older.Tags, dimension))
            {
                continue;
            }

            await _recommendations.AddAsync(new RuleLifecycleRecommendation
            {
                RuleId = older.Id,
                TargetRuleId = newer.Id,
                RecommendationType = RecommendationType.Supersede,
                Reason = $"A newer explicit {dimension} preference (#{newer.Id}) replaces this one.",
                Evidence = $"Both rules set the user's {dimension} preference; the newer one is #{newer.Id}.",
                Confidence = UserPreferenceConfidence,
                Signature = $"Supersede:{older.Id}:{newer.Id}",
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool HasDimensionTag(string? tags, string dimension) =>
        (tags ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(t => string.Equals(t, dimension, StringComparison.OrdinalIgnoreCase));

    private async Task<RecallRule?> FindEquivalentAsync(RecallRule candidate, CancellationToken cancellationToken)
    {
        var key = NormalizeGuidance(candidate.RuleText);
        if (key.Length == 0)
        {
            return null;
        }

        // Scope and status filtering runs in the database; only the small same-scope set
        // is loaded, and the in-memory step is just the normalized-guidance equivalence
        // (which is not expressible in SQL).
        var sameScope = await _rules.QueryAsync(new RuleQuery
        {
            ScopeLevel = candidate.ScopeLevel,
            ScopeValue = candidate.ScopeValue ?? string.Empty,
            ExcludeStatuses = NotReusable,
        }, cancellationToken).ConfigureAwait(false);

        return sameScope.FirstOrDefault(r => NormalizeGuidance(r.RuleText) == key);
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
