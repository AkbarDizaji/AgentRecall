using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Policy;

/// <summary>
/// The situation a resolution runs against: the scope a task belongs to, used to
/// prefer project-specific rules over global ones.
/// </summary>
public sealed record PolicyContext
{
    /// <summary>The scope granularity of the task, if known.</summary>
    public ScopeLevel? ScopeLevel { get; init; }

    /// <summary>The scope identifier of the task (e.g. repo name), if known.</summary>
    public string? ScopeValue { get; init; }

    /// <summary>An empty context with no scope information.</summary>
    public static PolicyContext None { get; } = new();
}

/// <summary>Whether a matching rule was kept or set aside by the policy engine.</summary>
public enum RuleDecision
{
    Effective,
    Ignored,
}

/// <summary>A rule together with the decision the policy engine made about it.</summary>
public sealed record RuleVerdict
{
    public required RecallRule Rule { get; init; }
    public required RuleDecision Decision { get; init; }

    /// <summary>Why the rule was kept or ignored, in plain language.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// A resolved conflict between rules that gave opposing guidance on the same
/// subject: the rule that won and the ones that were ignored as a result.
/// </summary>
public sealed record RuleConflict
{
    public required RecallRule Winner { get; init; }
    public required IReadOnlyList<RecallRule> Losers { get; init; }

    /// <summary>The shared subject the rules disagreed on (e.g. "repository pattern").</summary>
    public required string Subject { get; init; }

    /// <summary>The resolution criterion that decided the winner.</summary>
    public required string Reason { get; init; }
}

/// <summary>
/// The outcome of resolving the rules that match a task: which rules are
/// effective, which are ignored (and why), the conflicts that were settled, and a
/// human-readable explanation.
/// </summary>
public sealed record PolicyResolution
{
    public required IReadOnlyList<RuleVerdict> Effective { get; init; }
    public required IReadOnlyList<RuleVerdict> Ignored { get; init; }
    public required IReadOnlyList<RuleConflict> Conflicts { get; init; }
    public required string Explanation { get; init; }
}
