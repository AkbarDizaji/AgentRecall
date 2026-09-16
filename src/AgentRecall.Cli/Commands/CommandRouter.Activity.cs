using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Text;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli;

// The `activity` command group: reads the human-visible activity log.
public static partial class CommandRouter
{
    /// <summary>
    /// Shows the human-visible activity log: what AgentRecall recently fetched,
    /// captured, skipped, resolved, mined, or recommended. Reads only — it never
    /// records its own activity, so querying the log can never spam it.
    /// </summary>
    private static async Task<int> ActivityAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var sub = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "last";
        var options = ParseOptions(args);
        var json = options.ContainsKey("json");

        if (sub is not ("last" or "list"))
        {
            output.WriteLine("Usage:");
            output.WriteLine("  agentrecall activity last [--json]");
            output.WriteLine("  agentrecall activity list [--limit <n>] [--json]");
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var recorder = scope.ServiceProvider.GetRequiredService<IActivityRecorder>();

        if (sub == "last")
        {
            var last = await recorder.GetLastAsync(cancellationToken).ConfigureAwait(false);
            if (json)
            {
                WriteJson(output, last is null ? null : ActivityJson(last));
                return 0;
            }

            if (last is null)
            {
                output.WriteLine($"{ActivityNoticeRenderer.Badge} no activity recorded yet.");
                return 0;
            }

            // The explicit `activity` query always shows full detail, independent of the
            // configured notice level (that level governs automatic notices, not lookups).
            output.WriteLine(ActivityNoticeRenderer.Render(ActivityNotice.FromEntity(last), NoticeLevel.Verbose));
            return 0;
        }

        if (!TryReadIntOption(options, "limit", output, out var limit))
        {
            return 1;
        }

        var recent = await recorder.ListAsync(limit ?? 10, cancellationToken).ConfigureAwait(false);
        if (json)
        {
            WriteJson(output, recent.Select(ActivityJson).ToList());
            return 0;
        }

        if (recent.Count == 0)
        {
            output.WriteLine($"{ActivityNoticeRenderer.Badge} no activity recorded yet.");
            return 0;
        }

        // Newest first, one compact line each.
        foreach (var activity in recent)
        {
            output.WriteLine(ActivityNoticeRenderer.Render(ActivityNotice.FromEntity(activity), NoticeLevel.Normal));
        }

        return 0;
    }

    /// <summary>
    /// Projects an activity to its JSON shape. Fields are plain (no Markdown); the only
    /// styled value is <c>rendered_notice</c>, a compact one-line render.
    /// </summary>
    private static object ActivityJson(AgentRecallActivity activity) => new
    {
        id = activity.Id,
        timestamp = activity.CreatedAt.ToString("O"),
        type = activity.ActivityType.ToString(),
        summary = activity.Summary,
        details = string.IsNullOrEmpty(activity.Details)
            ? Array.Empty<string>()
            : activity.Details.Split('\n', StringSplitOptions.RemoveEmptyEntries),
        ruleIds = IdList.Parse(activity.RuleIds).ToArray(),
        candidateIds = IdList.Parse(activity.CandidateIds).ToArray(),
        recommendationIds = IdList.Parse(activity.RecommendationIds).ToArray(),
        source = activity.Source,
        noticeLevel = activity.NoticeLevel.ToString(),
        operationHash = activity.OperationHash,
        renderedNotice = ActivityNoticeRenderer.RenderCompact(ActivityNotice.FromEntity(activity), NoticeLevel.Normal),
    };
}
