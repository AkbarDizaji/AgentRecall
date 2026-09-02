using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Outcomes;

/// <summary>
/// One outcome the host reported for a rule AgentRecall injected: how the rule actually fared.
/// The agent is the only party that can observe this, so it is supplied the same way a capture
/// judgment is — AgentRecall validates it and stores it, and never infers one.
/// </summary>
public sealed record ReportedRuleOutcome
{
    /// <summary>The rule being reported on.</summary>
    public int? RuleId { get; init; }

    /// <summary>The retrieval it was injected by, when the reporter quotes the id back.</summary>
    public string? RetrievalId { get; init; }

    /// <summary>What happened.</summary>
    public required OutcomeType Outcome { get; init; }

    /// <summary>A short note recorded as the outcome's reason.</summary>
    public string? Note { get; init; }
}

/// <summary>What AgentRecall did with a turn's reported outcomes.</summary>
public sealed record TurnOutcomeReportResult
{
    /// <summary>Rules whose confidence moved, with the outcome that moved it.</summary>
    public IReadOnlyList<(int RuleId, OutcomeType Outcome)> Applied { get; init; } = [];

    /// <summary>Reports AgentRecall refused, each with the reason it was refused.</summary>
    public IReadOnlyList<string> Rejected { get; init; } = [];

    /// <summary>Rules this turn injected that nobody reported on.</summary>
    public IReadOnlyList<int> Unreported { get; init; } = [];

    /// <summary>True when the turn injected rules at all — nothing is expected when it did not.</summary>
    public bool TurnUsedRules { get; init; }

    /// <summary>True when outcome tracking is switched off, so nothing was recorded either way.</summary>
    public bool Disabled { get; init; }

    public bool IsEmpty => Applied.Count == 0 && Rejected.Count == 0 && Unreported.Count == 0;
}

/// <summary>
/// Validates and applies the outcomes a host reported for one turn.
///
/// Outcomes are the other half of recall: rules are injected on every turn, but nothing moves
/// their confidence unless someone says how they fared. AgentRecall cannot observe that itself,
/// so it asks — and then refuses to take the answer on trust. A report only counts for a rule a
/// retrieval actually injected, and only for the outcomes an agent can honestly witness.
/// </summary>
public interface ITurnOutcomeReporter
{
    /// <summary>
    /// Applies <paramref name="reports"/> for the turn identified by <paramref name="turnId"/>,
    /// and works out which injected rules were left unreported.
    /// </summary>
    Task<TurnOutcomeReportResult> ApplyAsync(
        string? turnId,
        IReadOnlyList<ReportedRuleOutcome> reports,
        CancellationToken cancellationToken = default);
}
