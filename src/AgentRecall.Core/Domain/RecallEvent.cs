namespace AgentRecall.Core.Domain;

/// <summary>
/// An append-only record of something that happened — a rule being created,
/// applied, superseded, or a mistake being observed.
/// </summary>
public sealed class RecallEvent
{
    public int Id { get; set; }

    public RecallEventType Type { get; set; }

    /// <summary>The rule this event relates to, if any.</summary>
    public int? RuleId { get; set; }

    /// <summary>What triggered or prompted the event.</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>Free-form details about the event.</summary>
    public string Details { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
