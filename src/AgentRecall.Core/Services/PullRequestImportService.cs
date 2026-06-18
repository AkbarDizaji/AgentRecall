using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Feedback;

namespace AgentRecall.Core.Services;

/// <summary>
/// Default <see cref="IPullRequestImportService"/>. Gates each comment through the
/// <see cref="IFeedbackCandidateAnalyzer"/> so only reusable corrections are kept,
/// then records the kept ones through <see cref="IFeedbackService"/> — giving each
/// a pending rule and an audit event, exactly like manually-captured feedback.
/// </summary>
public sealed class PullRequestImportService : IPullRequestImportService
{
    /// <summary>Tag applied to every rule learned from a pull request.</summary>
    public const string SourceTag = "pr-review";

    private readonly IFeedbackService _feedback;
    private readonly IFeedbackCandidateAnalyzer _analyzer;

    public PullRequestImportService(IFeedbackService feedback, IFeedbackCandidateAnalyzer analyzer)
    {
        _feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
    }

    public async Task<PullRequestImportResult> ImportAsync(
        IReadOnlyList<PullRequestComment> comments,
        PullRequestImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comments);
        options ??= new PullRequestImportOptions();

        var ruleIds = new List<int>();
        var skipped = 0;

        foreach (var comment in comments)
        {
            // Only actionable corrections become rules; praise/questions/nits are skipped.
            if (string.IsNullOrWhiteSpace(comment.Body) || !_analyzer.Analyze(comment.Body).IsCandidate)
            {
                skipped++;
                continue;
            }

            var result = await _feedback.AddAsync(new FeedbackInput
            {
                Task = TaskContext(comment, options),
                Feedback = comment.Body,
                ScopeLevel = options.ScopeLevel,
                ScopeValue = options.ScopeValue,
                Tags = Tags(options.Tags),
                // Accepted comments (the user acted on them) are recorded Active;
                // otherwise a bulk import stays Pending for explicit review,
                // regardless of the global auto-approve default.
                AutoApprove = options.Accepted ? true : false,
            }, cancellationToken).ConfigureAwait(false);

            // The worthiness policy can reject a code fact even on an accepted
            // comment — acceptance does not bypass it. Count those as skipped.
            if (result.Rule is null)
            {
                skipped++;
                continue;
            }

            ruleIds.Add(result.Rule.Id);
        }

        return new PullRequestImportResult(comments.Count, ruleIds.Count, skipped, ruleIds);
    }

    public async Task<PullRequestImportResult> ImportFileAsync(
        string filePath,
        PullRequestImportOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A comments file path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Comments file not found: {filePath}", filePath);
        }

        var content = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        var comments = PullRequestCommentParser.Parse(content);
        return await ImportAsync(comments, options, cancellationToken).ConfigureAwait(false);
    }

    private static string TaskContext(PullRequestComment comment, PullRequestImportOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PullRequestTitle))
        {
            return options.PullRequestTitle!.Trim();
        }

        return string.IsNullOrWhiteSpace(comment.Path) ? "pull request review" : $"reviewing {comment.Path}";
    }

    private static string Tags(string? extra) =>
        string.IsNullOrWhiteSpace(extra) ? SourceTag : $"{SourceTag},{extra}";
}
