namespace AgentRecall.Core.Feedback;

/// <summary>
/// A single review comment from a pull request — the raw material an import turns
/// into feedback (and, when it reads as a reusable correction, a rule).
/// </summary>
public sealed record PullRequestComment
{
    /// <summary>The comment text.</summary>
    public required string Body { get; init; }

    /// <summary>Who left the comment, if known.</summary>
    public string? Author { get; init; }

    /// <summary>The file the comment was left on, if it was a code comment.</summary>
    public string? Path { get; init; }
}
