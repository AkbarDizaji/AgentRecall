using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Feedback;

/// <summary>
/// Raw feedback captured from a user about an agent's behaviour on a task.
/// The source for both a stored <see cref="RecallEvent"/> and an extracted
/// <see cref="RecallRule"/>.
/// </summary>
public sealed record FeedbackInput
{
    /// <summary>What the agent was asked to do.</summary>
    public required string Task { get; init; }

    /// <summary>The corrective guidance the user gave.</summary>
    public required string Feedback { get; init; }

    /// <summary>The undesirable output, if provided.</summary>
    public string? BadOutput { get; init; }

    /// <summary>The corrected/preferred output, if provided.</summary>
    public string? FixedOutput { get; init; }

    /// <summary>Scope granularity this feedback applies to.</summary>
    public ScopeLevel ScopeLevel { get; init; } = ScopeLevel.Global;

    /// <summary>Scope identifier (e.g. repo name, language, path).</summary>
    public string? ScopeValue { get; init; }

    /// <summary>Comma-separated tags, if provided.</summary>
    public string? Tags { get; init; }

    /// <summary>
    /// Overrides the configured default: <c>true</c> forces an Active rule,
    /// <c>false</c> forces Pending. <c>null</c> uses
    /// <see cref="Configuration.AgentRecallOptions.AutoApproveFeedback"/>.
    /// </summary>
    public bool? AutoApprove { get; init; }
}
