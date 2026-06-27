using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Conflicts;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Memory;

namespace AgentRecall.Core.Finalization;

/// <summary>
/// Default <see cref="ITurnFinalizer"/>. It is the canonical capture path for a
/// completed turn: it extracts candidate lessons, ranks them, and routes each through
/// the same <see cref="IFeedbackService"/> every other capture flow uses — so the
/// auto-capture / suggest / skip decision, duplicate detection, and "lessons, not
/// facts" screening are all reused, never re-implemented. It records the outcome so it
/// can be queried, and is idempotent: re-finalizing the same turn creates no duplicates.
/// </summary>
public sealed class TurnFinalizer : ITurnFinalizer
{
    /// <summary>Tag applied to every rule captured by the turn finalizer.</summary>
    public const string SourceTag = "turn-finalizer";

    // Conflict kinds strong enough to hold a capture back for review.
    private static readonly HashSet<RuleConflictType> BlockingConflicts =
        [RuleConflictType.DirectOpposition, RuleConflictType.PreferredVsAvoided];

    private static readonly HashSet<RuleStatus> ActiveStatuses =
        [RuleStatus.Active, RuleStatus.Promoted];

    private readonly ITurnCandidateExtractor _extractor;
    private readonly IMemoryWorthinessClassifier _classifier;
    private readonly IRecallExtractor _ruleExtractor;
    private readonly IRuleConflictDetector _conflictDetector;
    private readonly IFeedbackService _feedback;
    private readonly IRecallRuleRepository _rules;
    private readonly ITurnFinalizationRepository _finalizations;
    private readonly AgentRecallOptions _options;

    public TurnFinalizer(
        ITurnCandidateExtractor extractor,
        IMemoryWorthinessClassifier classifier,
        IRecallExtractor ruleExtractor,
        IRuleConflictDetector conflictDetector,
        IFeedbackService feedback,
        IRecallRuleRepository rules,
        ITurnFinalizationRepository finalizations,
        AgentRecallOptions options)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _ruleExtractor = ruleExtractor ?? throw new ArgumentNullException(nameof(ruleExtractor));
        _conflictDetector = conflictDetector ?? throw new ArgumentNullException(nameof(conflictDetector));
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _finalizations = finalizations ?? throw new ArgumentNullException(nameof(finalizations));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<TurnFinalizationResult> FinalizeAsync(
        TurnFinalizationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!_options.TurnFinalizerEnabled)
        {
            return new TurnFinalizationResult();
        }

        var hash = ComputeHash(input);

        // Idempotent: an identical turn that was already finalized returns the prior
        // result and creates nothing new (the Stop hook may fire more than once).
        var prior = await FindByHashAsync(hash, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            var reconstructed = await ReconstructAsync(prior, cancellationToken).ConfigureAwait(false);
            return reconstructed with { FromCache = true };
        }

        var captured = new List<FinalizedLesson>();
        var suggested = new List<FinalizedLesson>();
        var skipped = new List<SkippedLesson>();
        var duplicates = new List<int>();
        var errors = new List<string>();

        try
        {
            await ProcessAsync(input, captured, suggested, skipped, duplicates, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block the turn; record the problem and persist what we have.
            errors.Add(ex.Message);
        }

        // A turn with no lesson at all (ordinary coding work) is not persisted, so the
        // last finalization stays the last one that actually decided something — a more
        // useful answer to "did this turn produce a lesson?" than an empty record.
        var producedSomething =
            captured.Count > 0 || suggested.Count > 0 || skipped.Count > 0 ||
            duplicates.Count > 0 || errors.Count > 0;

        int? id = null;
        if (producedSomething)
        {
            var finalization = new TurnFinalization
            {
                Cwd = input.Cwd ?? string.Empty,
                Source = input.Source,
                CapturedRuleIds = Join(captured.Select(c => c.RuleId)),
                SuggestedRuleIds = Join(suggested.Select(s => s.RuleId)),
                SkippedReasons = string.Join('\n', skipped.Select(s => s.Reason)),
                DuplicateRuleIds = Join(duplicates),
                ErrorSummary = string.Join("; ", errors),
                RawHash = hash,
                Transcript = _options.StoreTurnTranscript ? input.RawTranscript ?? string.Empty : string.Empty,
            };

            try
            {
                var stored = await _finalizations.AddAsync(finalization, cancellationToken).ConfigureAwait(false);
                id = stored.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"Could not persist finalization: {ex.Message}");
            }
        }

        return new TurnFinalizationResult
        {
            Captured = captured,
            Suggested = suggested,
            Skipped = skipped,
            Duplicates = duplicates,
            Errors = errors,
            Id = id,
            Source = input.Source,
        };
    }

    private async Task ProcessAsync(
        TurnFinalizationInput input,
        List<FinalizedLesson> captured,
        List<FinalizedLesson> suggested,
        List<SkippedLesson> skipped,
        List<int> duplicates,
        CancellationToken cancellationToken)
    {
        var userText = input.Prompt;
        var assistantText = input.AssistantResponse;

        var acceptance = _extractor.HasAcceptanceSignal(userText) || input.Accepted == true;

        // Outcome-aware evidence for the turn: an observed failure, a user correction, an
        // accepted review, a test that failed then passed, or a repeat. These let the
        // adaptive policy elevate a generic lesson that names a real mistake.
        var outcome = _extractor.DetectOutcomeSignals(userText, assistantText);

        // A "do not save" turn is honoured unless the user also explicitly accepted a
        // correction this turn (a stronger, contradicting signal).
        if (_extractor.HasDoNotSaveSignal(userText, assistantText) && !acceptance)
        {
            skipped.Add(new SkippedLesson { Reason = "Turn contained a do-not-save signal; nothing captured." });
            return;
        }

        var candidates = _extractor
            .Extract(userText, assistantText, _options.MaxCandidateCharacters)
            .OrderByDescending(c => c.Priority)
            .Take(Math.Max(0, _options.MaxCandidatesPerTurn))
            .ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RouteAsync(candidate, input, acceptance, outcome, captured, suggested, skipped, duplicates, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RouteAsync(
        TurnLessonCandidate candidate,
        TurnFinalizationInput input,
        bool acceptance,
        TurnOutcomeSignals outcome,
        List<FinalizedLesson> captured,
        List<FinalizedLesson> suggested,
        List<SkippedLesson> skipped,
        List<int> duplicates,
        CancellationToken cancellationToken)
    {
        // Classify here only to decide the posture we hand the decision policy; the
        // FeedbackService classifies again and owns the actual capture decision.
        var worthiness = _classifier.Classify(candidate.Text);

        // A generic textbook rule (worthy but unremarkable: low confidence, not
        // conditional, no security angle, and naming no concrete repository symbol) is
        // parked for review rather than activated unilaterally.
        var generic = worthiness.Verdict == MemoryWorthiness.WorthStoring &&
                      worthiness.Confidence <= _options.CaptureAutoConfidence &&
                      !candidate.Security &&
                      !candidate.Conditional &&
                      !candidate.HasSymbol;

        // Hold a capture back when it directly contradicts an existing active rule.
        var conflict = await DetectConflictAsync(candidate, input, worthiness.Category, cancellationToken)
            .ConfigureAwait(false);

        bool? autoApprove =
            acceptance ? true
            : conflict is not null ? false
            : generic ? false
            : null;

        // When the turn carries outcome-aware evidence, hand it to the adaptive policy so
        // an observed mistake can elevate a generic lesson and a code fact is still never
        // auto-captured. With no such evidence, the context is null and capture behaves
        // exactly as before (text worthiness alone), preserving the existing decisions.
        var context = outcome.HasAny
            ? new CaptureContext
            {
                Source = SourceTag,
                AcceptanceSignal = acceptance,
                ExplicitSaveRequest = acceptance,
                ObservedFailure = outcome.ObservedFailure,
                UserCorrection = outcome.UserCorrection,
                ReviewAccepted = outcome.ReviewAccepted,
                TestFailedThenFixed = outcome.TestFailedThenFixed,
                RepeatedCorrectionCount = outcome.RepeatedCorrectionCount,
                ConflictExists = conflict is not null,
                EvidenceSummary = BuildEvidenceSummary(outcome, candidate.Text),
            }
            : null;

        var input2 = new FeedbackInput
        {
            Task = BuildTask(input),
            Feedback = candidate.Text,
            ScopeLevel = input.ScopeLevel,
            ScopeValue = input.ScopeValue,
            Tags = SourceTag,
            AutoApprove = autoApprove,
            Context = context,
        };

        var result = await _feedback.AddAsync(input2, cancellationToken).ConfigureAwait(false);
        var decision = result.Decision;
        var note = conflict is not null
            ? $"Conflicts with rule #{OtherRuleId(conflict)}: {conflict.Summary}"
            : null;

        switch (decision?.Outcome)
        {
            case CaptureOutcome.AutoCapture when result.Rule is { } rule:
                captured.Add(ToLesson(rule, decision, note));
                break;

            case CaptureOutcome.SuggestCapture when result.Rule is { } rule:
                suggested.Add(ToLesson(rule, decision, note ?? decision.Notice));
                break;

            case CaptureOutcome.Skip:
                if (result.ReusedExistingRule && result.Rule is { } existing)
                {
                    duplicates.Add(existing.Id);
                    skipped.Add(new SkippedLesson
                    {
                        Reason = $"Duplicate of rule #{existing.Id}.",
                        DuplicateOfRuleId = existing.Id,
                    });
                }
                else
                {
                    skipped.Add(new SkippedLesson { Reason = decision?.Reason ?? "Not stored." });
                }

                break;

            default:
                // No decision (legacy path) or no rule — record a generic skip.
                skipped.Add(new SkippedLesson { Reason = decision?.Reason ?? "Nothing stored." });
                break;
        }
    }

    private async Task<RuleConflict?> DetectConflictAsync(
        TurnLessonCandidate candidate,
        TurnFinalizationInput input,
        RuleCategory category,
        CancellationToken cancellationToken)
    {
        var all = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        var active = all
            .Where(r => ActiveStatuses.Contains(r.Status) && !r.Deprecated)
            .ToList();
        if (active.Count == 0)
        {
            return null;
        }

        var candidateRule = _ruleExtractor.Extract(new FeedbackInput
        {
            Task = BuildTask(input),
            Feedback = candidate.Text,
            ScopeLevel = input.ScopeLevel,
            ScopeValue = input.ScopeValue,
        });

        // A sentinel id no real rule can hold, so the candidate is identifiable in the
        // detector's pairwise output without colliding with a stored rule.
        candidateRule.Id = int.MaxValue;
        candidateRule.Category = category;

        var pool = new List<RecallRule>(active) { candidateRule };
        return _conflictDetector
            .Detect(pool)
            .FirstOrDefault(c => c.RuleIds.Contains(int.MaxValue) && BlockingConflicts.Contains(c.ConflictType));
    }

    private static int OtherRuleId(RuleConflict conflict) =>
        conflict.RuleIds.FirstOrDefault(id => id != int.MaxValue);

    public async Task<TurnFinalizationResult?> GetLastAsync(
        string? cwd = null,
        CancellationToken cancellationToken = default)
    {
        var all = await _finalizations.ListAsync(cancellationToken).ConfigureAwait(false);
        var last = all
            .Where(f => cwd is null || string.Equals(f.Cwd, cwd, StringComparison.Ordinal))
            .OrderByDescending(f => f.CreatedAt)
            .ThenByDescending(f => f.Id)
            .FirstOrDefault();

        if (last is null)
        {
            return null;
        }

        return await ReconstructAsync(last, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TurnFinalizationResult> ReconstructAsync(
        TurnFinalization finalization,
        CancellationToken cancellationToken)
    {
        var captured = new List<FinalizedLesson>();
        foreach (var id in ParseIds(finalization.CapturedRuleIds))
        {
            if (await _rules.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } rule)
            {
                captured.Add(ToLesson(rule, decision: null, note: null));
            }
        }

        var suggested = new List<FinalizedLesson>();
        foreach (var id in ParseIds(finalization.SuggestedRuleIds))
        {
            if (await _rules.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } rule)
            {
                suggested.Add(ToLesson(rule, decision: null, note: null));
            }
        }

        var skipped = (finalization.SkippedReasons ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => new SkippedLesson { Reason = r })
            .ToList();

        var errors = string.IsNullOrWhiteSpace(finalization.ErrorSummary)
            ? Array.Empty<string>()
            : finalization.ErrorSummary.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new TurnFinalizationResult
        {
            Captured = captured,
            Suggested = suggested,
            Skipped = skipped,
            Duplicates = ParseIds(finalization.DuplicateRuleIds).ToList(),
            Errors = errors,
            Id = finalization.Id,
            CreatedAt = finalization.CreatedAt,
            Source = finalization.Source,
        };
    }

    private async Task<TurnFinalization?> FindByHashAsync(string hash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return null;
        }

        var all = await _finalizations.ListAsync(cancellationToken).ConfigureAwait(false);
        return all
            .Where(f => string.Equals(f.RawHash, hash, StringComparison.Ordinal))
            .OrderByDescending(f => f.CreatedAt)
            .ThenByDescending(f => f.Id)
            .FirstOrDefault();
    }

    private static FinalizedLesson ToLesson(RecallRule rule, CaptureDecision? decision, string? note) =>
        new()
        {
            RuleId = rule.Id,
            Category = rule.Category,
            Text = rule.RuleText,
            ScopeLabel = decision?.ScopeLabel ?? ScopeLabel(rule.ScopeLevel, rule.ScopeValue),
            Confidence = rule.Confidence,
            Note = note,
        };

    private static string ScopeLabel(ScopeLevel level, string? value) =>
        level == ScopeLevel.Global
            ? "Global"
            : string.IsNullOrWhiteSpace(value) ? level.ToString() : $"{level}:{value}";

    private static string BuildEvidenceSummary(TurnOutcomeSignals outcome, string candidateText)
    {
        var parts = new List<string>();
        if (outcome.ObservedFailure) parts.Add("the agent's output broke or changed behaviour");
        if (outcome.UserCorrection) parts.Add("the user corrected it");
        if (outcome.ReviewAccepted) parts.Add("a review comment was applied");
        if (outcome.TestFailedThenFixed) parts.Add("a test failed then passed");
        if (outcome.RepeatedCorrectionCount >= 2) parts.Add("the same correction recurred");

        var evidence = parts.Count > 0 ? string.Join("; ", parts) : "an observed outcome";
        var snippet = candidateText.Trim();
        if (snippet.Length > 120)
        {
            snippet = snippet[..119] + "…";
        }

        return $"Observed in turn: {evidence}. Lesson: {snippet}";
    }

    private static string BuildTask(TurnFinalizationInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.AssistantResponse))
        {
            var trimmed = input.AssistantResponse.Trim();
            return trimmed.Length <= 160 ? trimmed : trimmed[..159] + "…";
        }

        return string.IsNullOrWhiteSpace(input.ScopeValue) ? "turn finalization" : $"working in {input.ScopeValue}";
    }

    private static string ComputeHash(TurnFinalizationInput input)
    {
        var payload = string.Join(
            '',
            input.Cwd ?? string.Empty,
            input.Source ?? string.Empty,
            input.Prompt ?? string.Empty,
            input.AssistantResponse ?? string.Empty);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes);
    }

    private static string Join(IEnumerable<int> ids) =>
        string.Join(',', ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    private static IEnumerable<int> ParseIds(string? csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (int?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value);
}
