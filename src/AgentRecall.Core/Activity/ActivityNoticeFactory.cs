using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Conflicts;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;
using AgentRecall.Core.Mining;

namespace AgentRecall.Core.Activity;

/// <summary>
/// Builds <see cref="ActivityNotice"/> values from the result types AgentRecall's
/// operations produce. Each builder returns null when there is nothing worth telling
/// the user, so callers never emit (or persist) no-op spam. Summaries and details are
/// plain text — styling is added later by <see cref="ActivityNoticeRenderer"/>.
/// </summary>
public static class ActivityNoticeFactory
{
    private const int LabelLength = 60;

    /// <summary>A notice for a context-fetch result, or null when nothing was injected.</summary>
    public static ActivityNotice? ForContextFetched(ContextInjectionResult result, string source)
    {
        ArgumentNullException.ThrowIfNull(result);

        var rules = result.All.ToList();
        if (rules.Count == 0)
        {
            return null;
        }

        return new ActivityNotice
        {
            Type = ActivityType.ContextFetched,
            Summary = $"fetched {rules.Count} relevant {Plural(rules.Count, "rule")}.",
            Details = rules.Select(r => $"#{r.Rule.Id} {Short(r.Rule)}").ToList(),
            RuleIds = rules.Select(r => r.Rule.Id).ToList(),
            Source = source,
            // A retrieval is unique; key on its id so re-rendering the same retrieval
            // (if it ever recurs) does not double-log.
            OperationHash = result.RetrievalId is null ? null : $"context:{result.RetrievalId}",
        };
    }

    /// <summary>A notice for resolved conflicts, or null when nothing conflicted.</summary>
    public static ActivityNotice? ForConflictResolved(IReadOnlyList<ResolvedConflict> conflicts, string source)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        if (conflicts.Count == 0)
        {
            return null;
        }

        var ruleIds = conflicts.Select(c => c.Selected.Id).Distinct().ToList();
        return new ActivityNotice
        {
            Type = ActivityType.ConflictResolved,
            Summary = $"resolved {conflicts.Count} rule {Plural(conflicts.Count, "conflict")}.",
            Details = conflicts.Select(DescribeConflict).ToList(),
            RuleIds = ruleIds,
            Source = source,
        };
    }

    /// <summary>A notice for a finalized turn, or null when the turn produced nothing.</summary>
    public static ActivityNotice? ForTurnFinalized(TurnFinalizationResult result, string source)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsEmpty)
        {
            return null;
        }

        var realSkips = result.Skipped.Where(s => s.DuplicateOfRuleId is null).ToList();
        var duplicateSkips = result.Skipped.Where(s => s.DuplicateOfRuleId is not null).ToList();

        var parts = new List<string>();
        if (result.Captured.Count > 0) parts.Add($"captured {result.Captured.Count}");
        if (result.Suggested.Count > 0) parts.Add($"suggested {result.Suggested.Count}");
        if (realSkips.Count > 0) parts.Add($"skipped {realSkips.Count}");
        if (duplicateSkips.Count > 0) parts.Add($"reinforced {duplicateSkips.Count} duplicate");
        if (parts.Count == 0)
        {
            return null;
        }

        var details = new List<string>();
        details.AddRange(result.Captured.Select(l => $"#{l.RuleId} captured: {Trim(l.Text)}"));
        details.AddRange(result.Suggested.Select(l => $"#{l.RuleId} suggested: {Trim(l.Text)}"));
        details.AddRange(result.Skipped.Select(s => $"skipped: {s.Reason}"));

        return new ActivityNotice
        {
            Type = ActivityType.TurnFinalized,
            Summary = $"finalized turn — {string.Join(" · ", parts)}.",
            Details = details,
            RuleIds = result.Captured.Concat(result.Suggested).Select(l => l.RuleId).ToList(),
            Source = source,
            // Dedup on the persisted finalization id so a cached re-finalization logs once.
            OperationHash = result.Id is null ? null : $"turn:{result.Id}",
        };
    }

    /// <summary>
    /// A notice for a feedback-capture outcome (captured / suggested / skipped), or
    /// null when there is nothing to say (e.g. a silent duplicate with no decision).
    /// </summary>
    public static ActivityNotice? ForFeedback(FeedbackResult result, string source)
    {
        ArgumentNullException.ThrowIfNull(result);

        var decision = result.Decision;
        var ruleIds = result.Rule is { } rule ? new List<int> { rule.Id } : [];

        switch (decision?.Outcome)
        {
            case CaptureOutcome.AutoCapture when result.Rule is { } captured:
                return new ActivityNotice
                {
                    Type = ActivityType.RuleCaptured,
                    Summary = "captured 1 new rule.",
                    Details = [$"#{captured.Id} {Truncate(captured.RuleText, LabelLength)}"],
                    RuleIds = ruleIds,
                    Source = source,
                };

            case CaptureOutcome.SuggestCapture when result.Rule is { } suggested:
                return new ActivityNotice
                {
                    Type = ActivityType.RuleSuggested,
                    Summary = "suggested 1 rule for review.",
                    Details =
                    [
                        $"#{suggested.Id} {Truncate(suggested.RuleText, LabelLength)}",
                        $"reason: {decision!.Reason}",
                    ],
                    RuleIds = ruleIds,
                    Source = source,
                };

            case CaptureOutcome.Skip:
            {
                var (summary, reason) = result.ReusedExistingRule
                    ? ("skipped 1 duplicate.", "reinforced an existing rule")
                    : ("skipped 1 candidate.", decision?.Reason ?? result.Worthiness?.Reason ?? "not memory-worthy");
                return new ActivityNotice
                {
                    Type = ActivityType.CandidateSkipped,
                    Summary = summary,
                    Details = [reason],
                    RuleIds = ruleIds,
                    Source = source,
                };
            }

            default:
                // Legacy path with no decision: only speak if a rule was actually stored.
                return result.Rule is { } legacy
                    ? new ActivityNotice
                    {
                        Type = ActivityType.RuleCaptured,
                        Summary = "captured 1 new rule.",
                        Details = [$"#{legacy.Id} {Truncate(legacy.RuleText, LabelLength)}"],
                        RuleIds = [legacy.Id],
                        Source = source,
                    }
                    : null;
        }
    }

    /// <summary>A notice for a mining run, or null when nothing new was found.</summary>
    public static ActivityNotice? ForLessonsMined(MiningResult result, string source)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Suggested.Count == 0 && result.Created == 0 && result.Updated == 0)
        {
            return null;
        }

        var count = result.Suggested.Count;
        return new ActivityNotice
        {
            Type = ActivityType.LessonMined,
            Summary = $"mined {count} lesson {Plural(count, "candidate")}.",
            Details = result.Suggested
                .Take(5)
                .Select(c => $"#{c.Id} {Truncate(c.Title, LabelLength)} (seen {c.OccurrenceCount}x)")
                .ToList(),
            CandidateIds = result.Suggested.Select(c => c.Id).ToList(),
            Source = source,
        };
    }

    /// <summary>A notice for lifecycle recommendations, or null when none were produced.</summary>
    public static ActivityNotice? ForLifecycle(IReadOnlyList<RuleLifecycleRecommendation> recommendations, string source)
    {
        ArgumentNullException.ThrowIfNull(recommendations);
        if (recommendations.Count == 0)
        {
            return null;
        }

        var byType = recommendations
            .GroupBy(r => r.RecommendationType)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();

        return new ActivityNotice
        {
            Type = ActivityType.LifecycleRecommended,
            Summary = $"suggested {recommendations.Count} lifecycle {Plural(recommendations.Count, "action")}.",
            Details = byType,
            RecommendationIds = recommendations.Select(r => r.Id).Where(id => id > 0).ToList(),
            RuleIds = recommendations.Select(r => r.RuleId).Distinct().ToList(),
            Source = source,
        };
    }

    private static string DescribeConflict(ResolvedConflict conflict)
    {
        var ignored = conflict.Resolution.IgnoredRuleIds;
        var over = ignored.Count > 0 ? $" over {string.Join(", ", ignored.Select(id => $"#{id}"))}" : string.Empty;
        var why = conflict.Resolution.Explanation.Count > 0
            ? $" — {conflict.Resolution.Explanation[0]}"
            : string.Empty;
        return $"chose #{conflict.Resolution.SelectedRuleId}{over}{why}";
    }

    private static string Short(RecallRule rule)
    {
        var label = !string.IsNullOrWhiteSpace(rule.Trigger) ? rule.Trigger : rule.RuleText;
        return Truncate(label, LabelLength);
    }

    private static string Trim(string text) => Truncate((text ?? string.Empty).Trim(), LabelLength);

    private static string Truncate(string value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    private static string Plural(int count, string singular) => count == 1 ? singular : singular + "s";
}
