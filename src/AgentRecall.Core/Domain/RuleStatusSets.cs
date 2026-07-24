namespace AgentRecall.Core.Domain;

/// <summary>
/// The single source of truth for which <see cref="RuleStatus"/> values count as in-force
/// versus dead, so the policy engine, context injection, search, and deduplication all
/// agree. A rule is one of: Draft (not yet reviewed), Pending (awaiting approval), Active,
/// Promoted (the two in-force states), or Superseded / Retired / Archived (dead).
/// </summary>
public static class RuleStatusSets
{
    /// <summary>
    /// Statuses treated as in force — applied by the policy engine and eligible for
    /// context injection as must-follow guidance. Draft and Pending are deliberately
    /// excluded: they are never auto-applied as authoritative. Pending rules can
    /// still resurface as a capped, marked-pending Suggested entry (the hook does
    /// this by default via <see cref="AgentRecall.Core.Context.ContextRequest.PendingCap"/>) so a
    /// repeated suggestion can be recognized and reinforced; other callers still
    /// require explicit opt-in and stay uncapped.
    /// </summary>
    public static readonly IReadOnlyCollection<RuleStatus> Effective =
        new HashSet<RuleStatus> { RuleStatus.Active, RuleStatus.Promoted };

    /// <summary>
    /// Dead statuses that are never surfaced in search and never reused as a dedup
    /// target: a superseded, retired, or archived rule has been deliberately taken out
    /// of circulation.
    /// </summary>
    public static readonly IReadOnlyCollection<RuleStatus> Inactive =
        new HashSet<RuleStatus> { RuleStatus.Superseded, RuleStatus.Retired, RuleStatus.Archived };

    /// <summary>True when the rule is in force (effective status and not deprecated).</summary>
    public static bool IsEffective(RecallRule rule) =>
        rule is not null && !rule.Deprecated && Effective.Contains(rule.Status);

    /// <summary>True when the status is dead (never surfaced in search or reused).</summary>
    public static bool IsInactive(RuleStatus status) => Inactive.Contains(status);
}
