using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Capture.Judge;

/// <summary>
/// The rule the judge distilled from a turn, in normalized, bounded parts. AgentRecall
/// builds a <see cref="RecallRule"/> from these fields rather than storing raw turn text —
/// <see cref="Condition"/> becomes the trigger, <see cref="Action"/> the rule text,
/// <see cref="Avoid"/> the mistake, and <see cref="Because"/> the technical context.
/// </summary>
public sealed record NormalizedRule
{
    /// <summary>A short title for the rule.</summary>
    public string? Title { get; init; }

    /// <summary>The condition under which the rule applies ("when …").</summary>
    public string? Condition { get; init; }

    /// <summary>The action the rule prescribes.</summary>
    public string? Action { get; init; }

    /// <summary>The anti-pattern to avoid, when the rule is framed as a prohibition.</summary>
    public string? Avoid { get; init; }

    /// <summary>Why the rule matters — the rationale or consequence.</summary>
    public string? Because { get; init; }

    /// <summary>The scope the rule belongs to (e.g. a repository name), when the judge names one.</summary>
    public string? Scope { get; init; }

    /// <summary>
    /// True when the judge classified this as a universal constraint — a style, tone, process,
    /// or quality rule that applies to every task rather than a contextual lesson tied to a
    /// domain. Universal rules are delivered on every turn (see
    /// <see cref="Domain.RecallRule.AlwaysApply"/>), so one correction is enough. Preferences
    /// are treated as universal even when the judge leaves this false.
    /// </summary>
    public bool AlwaysApply { get; init; }

    /// <summary>Free-form tags the judge attached to the rule.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>
/// The semantic judge's structured verdict for a turn — deserialized from the strict JSON the
/// host model produces. AgentRecall validates this (see <c>CaptureJudgeValidator</c>) and maps
/// it to a persist action (see <c>CaptureJudgeDecisionMapper</c>); it is never trusted blindly.
/// </summary>
public sealed record CaptureJudgeVerdict
{
    /// <summary>What to do with the turn.</summary>
    public JudgeDecision Decision { get; init; }

    /// <summary>The kind of memory, when the decision stores one.</summary>
    public JudgeMemoryType MemoryType { get; init; }

    /// <summary>The judge's confidence in the decision, in [0, 1].</summary>
    public double Confidence { get; init; }

    /// <summary>Why the judge decided as it did.</summary>
    public JudgeCaptureReason CaptureReason { get; init; }

    /// <summary>The existing rule to reinforce or supersede, when applicable.</summary>
    public int? TargetExistingRuleId { get; init; }

    /// <summary>The normalized rule to store, when the decision captures/suggests/supersedes.</summary>
    public NormalizedRule? NormalizedRule { get; init; }

    /// <summary>A short account of the evidence behind the decision.</summary>
    public string? Evidence { get; init; }

    /// <summary>Why nothing was saved, required when the decision is <see cref="JudgeDecision.Skip"/>.</summary>
    public string? WhyNotSaved { get; init; }

    /// <summary>Notes on the duplicate match, required when reinforcing an existing rule.</summary>
    public string? DedupeNotes { get; init; }
}

/// <summary>An existing rule surfaced to the judge so it can dedupe/reinforce rather than duplicate.</summary>
public sealed record JudgeRelevantRule
{
    /// <summary>The rule's id.</summary>
    public required int Id { get; init; }

    /// <summary>A short title/summary of the rule.</summary>
    public required string Title { get; init; }

    /// <summary>The rule's category, for the judge's context.</summary>
    public string? Category { get; init; }
}

/// <summary>
/// The bounded, structured payload AgentRecall hands the judge. It carries what the model
/// needs to decide — the turn's user/assistant text, the outcome/acceptance signals, the scope,
/// and the already-retrieved rules — and never huge logs, full files, or unbounded transcript.
/// <see cref="SuppliedVerdict"/> lets the default host-supplied judge return the verdict the host
/// already produced; a future live provider ignores it and computes its own.
/// </summary>
public sealed record CaptureJudgeInput
{
    /// <summary>The latest user message in the turn (bounded).</summary>
    public string? UserPrompt { get; init; }

    /// <summary>A bounded summary of the assistant's response.</summary>
    public string? AssistantSummary { get; init; }

    /// <summary>Where the turn came from (e.g. <c>stop_hook</c>).</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>True when the turn carried an explicit save/acceptance signal.</summary>
    public bool AcceptanceSignal { get; init; }

    /// <summary>The scope granularity the turn belongs to.</summary>
    public ScopeLevel ScopeLevel { get; init; }

    /// <summary>The scope identifier (e.g. repository name).</summary>
    public string? ScopeValue { get; init; }

    /// <summary>The rules already retrieved as relevant to the turn, for dedupe/reinforce.</summary>
    public IReadOnlyList<JudgeRelevantRule> RelevantRules { get; init; } = [];

    /// <summary>
    /// The verdict the host model already produced for this turn, when the host supplies it on
    /// the payload. The default <c>HostSuppliedCaptureJudge</c> returns exactly this; a live
    /// provider ignores it.
    /// </summary>
    public CaptureJudgeVerdict? SuppliedVerdict { get; init; }
}
