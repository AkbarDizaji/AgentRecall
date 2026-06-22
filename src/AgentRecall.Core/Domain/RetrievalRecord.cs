namespace AgentRecall.Core.Domain;

/// <summary>
/// A record that a set of rules was injected for a task. It gives later outcomes
/// something concrete to attach to — so effectiveness is measured against the
/// rules that were actually surfaced, not just a retrieval count.
/// </summary>
public sealed class RetrievalRecord
{
    public int Id { get; set; }

    /// <summary>Stable external id surfaced to callers so they can attach outcomes.</summary>
    public string RetrievalId { get; set; } = string.Empty;

    /// <summary>The task the rules were retrieved for.</summary>
    public string Task { get; set; } = string.Empty;

    /// <summary>Comma-separated ids of the rules that were injected.</summary>
    public string RuleIds { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
