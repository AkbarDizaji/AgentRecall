using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;

namespace AgentRecall.Core.Abstractions;

/// <summary>Options applied to every rule captured from a pull request.</summary>
public sealed record PullRequestImportOptions
{
    /// <summary>The PR title/number, used as task context for the captured rules.</summary>
    public string? PullRequestTitle { get; init; }

    public ScopeLevel ScopeLevel { get; init; } = ScopeLevel.Global;

    /// <summary>Scope identifier (e.g. repo name).</summary>
    public string? ScopeValue { get; init; }

    /// <summary>Extra comma-separated tags to add alongside the "pr-review" tag.</summary>
    public string? Tags { get; init; }

    /// <summary>
    /// Whether these comments have been accepted (the user acted on them), which
    /// records them as Active rules. Defaults to <c>false</c>, so a bulk import of
    /// not-yet-vetted review comments stays Pending for explicit review.
    /// </summary>
    public bool Accepted { get; init; }
}

/// <summary>Summary of what a pull-request import produced.</summary>
public sealed record PullRequestImportResult(
    int CommentsFound,
    int RulesCreated,
    int Skipped,
    IReadOnlyList<int> RuleIds);

/// <summary>
/// Turns pull-request review comments into feedback: each comment that reads as a
/// reusable correction becomes a <see cref="RecallEvent"/> and a pending
/// <see cref="RecallRule"/>. Comments that are not actionable (praise, questions,
/// nits) are skipped.
/// </summary>
public interface IPullRequestImportService
{
    /// <summary>Imports an already-parsed set of comments.</summary>
    Task<PullRequestImportResult> ImportAsync(
        IReadOnlyList<PullRequestComment> comments,
        PullRequestImportOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Reads and parses a comments file (JSON from <c>gh</c>, or plain text), then imports it.</summary>
    Task<PullRequestImportResult> ImportFileAsync(
        string filePath,
        PullRequestImportOptions options,
        CancellationToken cancellationToken = default);
}
