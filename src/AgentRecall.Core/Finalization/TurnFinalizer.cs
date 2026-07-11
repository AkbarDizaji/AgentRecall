using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Policy;
using AgentRecall.Core.Services;

namespace AgentRecall.Core.Finalization;

/// <summary>
/// Default <see cref="ITurnFinalizer"/>. It is the canonical capture path for a completed turn.
/// The decision of whether the turn holds memory-worthy content belongs to the semantic capture
/// judge (<see cref="ICaptureJudge"/>); this class only builds the bounded judge input, validates
/// the returned verdict, maps it through deterministic thresholds, and persists the result. It
/// never decides capture from keywords, and it never falls back to keyword capture: when the
/// judge is unavailable the turn is skipped and that is recorded.
///
/// It records the outcome so it can be queried, and is idempotent: re-finalizing the same turn
/// creates no duplicates.
/// </summary>
public sealed class TurnFinalizer : ITurnFinalizer
{
    /// <summary>Tag applied to every rule captured by the turn finalizer.</summary>
    public const string SourceTag = "turn-finalizer";

    /// <summary>Recorded as the decision source when the judge produced a verdict.</summary>
    public const string JudgeDecisionSource = "SemanticCaptureJudge";

    /// <summary>The skip reason recorded when no semantic judge verdict was available.</summary>
    public const string JudgeUnavailableMessage =
        "Semantic capture judge unavailable; no automatic capture performed.";

    // How many relevant existing rules to surface to the judge for dedupe/reinforce.
    private const int MaxRelevantRules = 12;

    private readonly ICaptureJudge _judge;
    private readonly IPolicyEngine _policy;
    private readonly IRuleLifecycleService _lifecycle;
    private readonly IFeedbackService _feedback;
    private readonly IRecallRuleRepository _rules;
    private readonly ITurnFinalizationRepository _finalizations;
    private readonly AgentRecallOptions _options;

    public TurnFinalizer(
        ICaptureJudge judge,
        IPolicyEngine policy,
        IRuleLifecycleService lifecycle,
        IFeedbackService feedback,
        IRecallRuleRepository rules,
        ITurnFinalizationRepository finalizations,
        AgentRecallOptions options)
    {
        _judge = judge ?? throw new ArgumentNullException(nameof(judge));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
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

        if (!_options.TurnFinalizerEnabled || _options.ResolvedCaptureJudgeMode == CaptureJudgeMode.Off)
        {
            return new TurnFinalizationResult();
        }

        var hash = ComputeHash(input);

        // The deterministic turn id (cwd + prompt) joins this capture to the retrieval
        // activity recorded at UserPromptSubmit. Distinct from the idempotency hash, which
        // also folds in the assistant response.
        var turnId = Activity.TurnCorrelation.Compute(input.Cwd, input.Prompt);

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
        var decision = TurnJudgeDecision.None;

        try
        {
            decision = await JudgeTurnAsync(input, captured, suggested, skipped, duplicates, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block the turn; record the problem and persist what we have.
            errors.Add(ex.Message);
        }

        // A turn with no lesson at all (ordinary coding work) is not persisted, so the
        // last finalization stays the last one that actually decided something.
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
                TurnId = turnId ?? string.Empty,
                Transcript = _options.StoreTurnTranscript ? input.RawTranscript ?? string.Empty : string.Empty,
                DecisionSource = decision.Source,
                JudgeDecision = decision.Decision,
                JudgeCaptureReason = decision.JudgeReason,
                JudgeConfidence = decision.Confidence ?? 0d,
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
            TurnId = turnId,
            DecisionSource = decision.Source,
            Decision = string.IsNullOrEmpty(decision.Decision) ? null : decision.Decision,
            JudgeReason = string.IsNullOrEmpty(decision.JudgeReason) ? null : decision.JudgeReason,
            JudgeConfidence = decision.Confidence,
            TargetRuleId = decision.TargetRuleId,
        };
    }

    /// <summary>
    /// Runs the semantic judge over the whole turn and applies its verdict. The judge decides;
    /// this method only builds the bounded input, validates + maps the verdict, and persists.
    /// </summary>
    private async Task<TurnJudgeDecision> JudgeTurnAsync(
        TurnFinalizationInput input,
        List<FinalizedLesson> captured,
        List<FinalizedLesson> suggested,
        List<SkippedLesson> skipped,
        List<int> duplicates,
        CancellationToken cancellationToken)
    {
        var relevant = await BuildRelevantRulesAsync(input, cancellationToken).ConfigureAwait(false);

        var judgeInput = new CaptureJudgeInput
        {
            UserPrompt = input.Prompt,
            AssistantSummary = BuildAssistantSummary(input),
            Source = input.Source,
            AcceptanceSignal = input.Accepted == true,
            ScopeLevel = input.ScopeLevel,
            ScopeValue = input.ScopeValue,
            RelevantRules = relevant,
            SuppliedVerdict = input.SuppliedJudgment,
        };

        var verdict = await _judge.JudgeAsync(judgeInput, cancellationToken).ConfigureAwait(false);
        if (verdict is null)
        {
            // No verdict: the judge is unavailable. Skip — never a keyword-driven fallback.
            skipped.Add(new SkippedLesson { Reason = JudgeUnavailableMessage });
            return TurnJudgeDecision.Unavailable;
        }

        var validation = CaptureJudgeValidator.Validate(verdict, judgeInput);
        var outcome = CaptureJudgeDecisionMapper.Map(verdict, validation);

        var summary = new TurnJudgeDecision
        {
            Source = JudgeDecisionSource,
            Decision = outcome.JudgeDecision,
            JudgeReason = outcome.JudgeReason,
            Confidence = outcome.Confidence,
        };

        switch (outcome.Action)
        {
            case JudgePersistAction.AutoCapture:
            case JudgePersistAction.Suggest:
                return await PersistCaptureAsync(input, outcome, captured, suggested, duplicates, skipped, summary, cancellationToken)
                    .ConfigureAwait(false);

            case JudgePersistAction.Supersede:
                return await SupersedeAsync(input, outcome, captured, duplicates, summary, cancellationToken)
                    .ConfigureAwait(false);

            case JudgePersistAction.Reinforce:
                return await ReinforceAsync(outcome, skipped, duplicates, summary, cancellationToken)
                    .ConfigureAwait(false);

            default:
                skipped.Add(new SkippedLesson { Reason = outcome.Reason });
                return summary;
        }
    }

    private async Task<TurnJudgeDecision> PersistCaptureAsync(
        TurnFinalizationInput input,
        CaptureJudgeOutcome outcome,
        List<FinalizedLesson> captured,
        List<FinalizedLesson> suggested,
        List<int> duplicates,
        List<SkippedLesson> skipped,
        TurnJudgeDecision summary,
        CancellationToken cancellationToken)
    {
        var request = BuildRequest(input, outcome);
        var result = await _feedback.AddJudgedAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.ReusedExistingRule && result.Rule is { } existing)
        {
            // The judge asked to capture, but the rule already exists: reinforce, don't duplicate.
            duplicates.Add(existing.Id);
            skipped.Add(new SkippedLesson
            {
                Reason = $"Reinforced existing rule #{existing.Id}.",
                DuplicateOfRuleId = existing.Id,
            });
            return summary with { TargetRuleId = existing.Id };
        }

        if (result.Rule is { } rule)
        {
            var lesson = ToLesson(rule, outcome.Reason);
            if (outcome.Action == JudgePersistAction.AutoCapture)
            {
                captured.Add(lesson);
            }
            else
            {
                suggested.Add(lesson);
            }
        }

        return summary;
    }

    private async Task<TurnJudgeDecision> SupersedeAsync(
        TurnFinalizationInput input,
        CaptureJudgeOutcome outcome,
        List<FinalizedLesson> captured,
        List<int> duplicates,
        TurnJudgeDecision summary,
        CancellationToken cancellationToken)
    {
        var request = BuildRequest(input, outcome);
        var result = await _feedback.AddJudgedAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.Rule is not { } rule)
        {
            return summary;
        }

        if (result.ReusedExistingRule)
        {
            duplicates.Add(rule.Id);
            return summary with { TargetRuleId = rule.Id };
        }

        var target = outcome.TargetRuleId;
        var note = outcome.Reason;
        if (target is { } targetId && targetId != rule.Id &&
            await _rules.GetAsync(targetId, cancellationToken).ConfigureAwait(false) is not null)
        {
            await _lifecycle.SupersedeAsync(targetId, rule.Id, cancellationToken).ConfigureAwait(false);
            note = $"Supersedes rule #{targetId}.";
        }

        captured.Add(ToLesson(rule, note));
        return summary with { TargetRuleId = target };
    }

    private async Task<TurnJudgeDecision> ReinforceAsync(
        CaptureJudgeOutcome outcome,
        List<SkippedLesson> skipped,
        List<int> duplicates,
        TurnJudgeDecision summary,
        CancellationToken cancellationToken)
    {
        if (outcome.TargetRuleId is not { } targetId ||
            await _rules.GetAsync(targetId, cancellationToken).ConfigureAwait(false) is not { } target)
        {
            skipped.Add(new SkippedLesson { Reason = "Reinforcement target no longer exists; nothing stored." });
            return summary;
        }

        await _lifecycle.ReinforceAsync(targetId, RuleLifecycleService.ReinforcementDelta, cancellationToken)
            .ConfigureAwait(false);

        // Repeated-correction backstop: the same mistake recurred against a rule that already
        // exists, so relevance-gated delivery was not enough — promote it to always-apply so it
        // is surfaced on every turn from now on. A no-op when it is already always-apply.
        var promoted = false;
        if (outcome.DomainReason == CaptureReason.RepeatedCorrection && !target.AlwaysApply)
        {
            target.AlwaysApply = true;
            await _rules.UpdateAsync(target, cancellationToken).ConfigureAwait(false);
            promoted = true;
        }

        duplicates.Add(targetId);
        skipped.Add(new SkippedLesson
        {
            Reason = promoted
                ? $"Reinforced existing rule #{targetId} and promoted it to a standing rule (repeated correction)."
                : $"Reinforced existing rule #{targetId}.",
            DuplicateOfRuleId = targetId,
        });
        return summary with { TargetRuleId = targetId };
    }

    private JudgedCaptureRequest BuildRequest(TurnFinalizationInput input, CaptureJudgeOutcome outcome)
    {
        var rule = BuildRule(input, outcome);
        return new JudgedCaptureRequest
        {
            Rule = rule,
            Status = outcome.Status,
            DomainReason = outcome.DomainReason,
            EvidenceSummary = string.IsNullOrWhiteSpace(rule.TechnicalContext) ? null : rule.TechnicalContext,
            TaskContext = BuildTask(input),
        };
    }

    /// <summary>Builds a <see cref="RecallRule"/> from the judge's normalized rule.</summary>
    private RecallRule BuildRule(TurnFinalizationInput input, CaptureJudgeOutcome outcome)
    {
        var normalized = outcome.Rule ?? new NormalizedRule();

        // A repeated mistake is the backstop: if the same correction recurred, the rule the
        // judge captured earlier plainly did not stick, so promote this one to always-apply.
        var alwaysApply = outcome.AlwaysApply || outcome.DomainReason == CaptureReason.RepeatedCorrection;

        // A rule that applies everywhere — a preference or any always-apply constraint — is
        // stored at Global scope; every other rule keeps the turn's derived repository scope.
        var appliesEverywhere =
            alwaysApply || outcome.Category is RuleCategory.UserPreference or RuleCategory.CommunicationPreference;
        var scopeLevel = appliesEverywhere ? ScopeLevel.Global : input.ScopeLevel;
        var scopeValue = appliesEverywhere ? string.Empty : input.ScopeValue ?? string.Empty;

        return new RecallRule
        {
            Version = 1,
            Status = RuleStatus.Pending,
            Category = outcome.Category,
            Trigger = Trim(normalized.Condition),
            RuleText = Trim(normalized.Action),
            Mistake = Trim(normalized.Avoid),
            TechnicalContext = Trim(normalized.Because),
            Tags = BuildTags(normalized.Tags),
            Confidence = outcome.Confidence,
            AlwaysApply = alwaysApply,
            ScopeLevel = scopeLevel,
            ScopeValue = scopeValue,
        };
    }

    private static string BuildTags(IReadOnlyList<string> tags)
    {
        var parts = new List<string> { SourceTag };
        parts.AddRange(tags.Select(t => t.Trim()).Where(t => t.Length > 0));
        return string.Join(",", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<JudgeRelevantRule>> BuildRelevantRulesAsync(
        TurnFinalizationInput input, CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await _policy.ResolveForTaskAsync(
                BuildTask(input),
                new PolicyContext { ScopeLevel = input.ScopeLevel, ScopeValue = input.ScopeValue },
                cancellationToken).ConfigureAwait(false);

            return resolution.Effective
                .Take(MaxRelevantRules)
                .Select(v => new JudgeRelevantRule
                {
                    Id = v.Rule.Id,
                    Title = ShortTitle(v.Rule),
                    Category = v.Rule.Category.ToString(),
                })
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Relevant-rule context is best-effort; the judge can still decide without it.
            return [];
        }
    }

    private static string ShortTitle(RecallRule rule)
    {
        var text = !string.IsNullOrWhiteSpace(rule.RuleText) ? rule.RuleText : rule.Trigger;
        text = (text ?? string.Empty).Trim();
        return text.Length <= 100 ? text : text[..99] + "…";
    }

    private string? BuildAssistantSummary(TurnFinalizationInput input)
    {
        if (string.IsNullOrWhiteSpace(input.AssistantResponse))
        {
            return null;
        }

        var trimmed = input.AssistantResponse.Trim();
        var max = Math.Max(0, _options.MaxCandidateCharacters);
        return max > 0 && trimmed.Length > max ? trimmed[..max] + "…" : trimmed;
    }

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
                captured.Add(ToLesson(rule, note: null));
            }
        }

        var suggested = new List<FinalizedLesson>();
        foreach (var id in ParseIds(finalization.SuggestedRuleIds))
        {
            if (await _rules.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } rule)
            {
                suggested.Add(ToLesson(rule, note: null));
            }
        }

        var skipped = (finalization.SkippedReasons ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => new SkippedLesson { Reason = r })
            .ToList();

        var errors = string.IsNullOrWhiteSpace(finalization.ErrorSummary)
            ? Array.Empty<string>()
            : finalization.ErrorSummary.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var duplicateIds = ParseIds(finalization.DuplicateRuleIds).ToList();

        return new TurnFinalizationResult
        {
            Captured = captured,
            Suggested = suggested,
            Skipped = skipped,
            Duplicates = duplicateIds,
            Errors = errors,
            Id = finalization.Id,
            CreatedAt = finalization.CreatedAt,
            Source = finalization.Source,
            TurnId = string.IsNullOrEmpty(finalization.TurnId) ? null : finalization.TurnId,
            DecisionSource = string.IsNullOrEmpty(finalization.DecisionSource) ? null : finalization.DecisionSource,
            Decision = string.IsNullOrEmpty(finalization.JudgeDecision) ? null : finalization.JudgeDecision,
            JudgeReason = string.IsNullOrEmpty(finalization.JudgeCaptureReason) ? null : finalization.JudgeCaptureReason,
            JudgeConfidence = finalization.DecisionSource == JudgeDecisionSource ? finalization.JudgeConfidence : null,
            TargetRuleId = duplicateIds.Count > 0 ? duplicateIds[0] : null,
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

    private static FinalizedLesson ToLesson(RecallRule rule, string? note) =>
        new()
        {
            RuleId = rule.Id,
            Category = rule.Category,
            Text = rule.RuleText,
            ScopeLabel = ScopeLabel(rule.ScopeLevel, rule.ScopeValue),
            Confidence = rule.Confidence,
            AlwaysApply = rule.AlwaysApply,
            Note = note,
        };

    private static string ScopeLabel(ScopeLevel level, string? value) =>
        level == ScopeLevel.Global
            ? "Global"
            : string.IsNullOrWhiteSpace(value) ? level.ToString() : $"{level}:{value}";

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

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>The judge decision metadata threaded through to persistence and status output.</summary>
    private readonly record struct TurnJudgeDecision
    {
        public string Source { get; init; }
        public string Decision { get; init; }
        public string JudgeReason { get; init; }
        public double? Confidence { get; init; }
        public int? TargetRuleId { get; init; }

        /// <summary>No judged decision (empty turn / disabled).</summary>
        public static TurnJudgeDecision None => new() { Source = "None", Decision = string.Empty, JudgeReason = string.Empty };

        /// <summary>The judge was unavailable for the turn.</summary>
        public static TurnJudgeDecision Unavailable => new() { Source = "None", Decision = string.Empty, JudgeReason = string.Empty };
    }
}
