using System.Text;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Services;

namespace AgentRecall.Core.Policy;

/// <summary>
/// Default <see cref="IPolicyEngine"/>. Given the rules that match a task it
/// removes rules that are not in force, settles explicit supersedes and direct
/// conflicts, and reports the effective rules, the ignored rules, and why.
///
/// When two rules compete, the winner is chosen by this precedence:
/// <list type="number">
///   <item>Project-specific rules over global rules.</item>
///   <item>A rule that explicitly supersedes the other.</item>
///   <item>Higher <see cref="RecallRule.Priority"/>.</item>
///   <item>Newer rule (more recent <see cref="RecallRule.CreatedAt"/>).</item>
///   <item>Higher <see cref="RecallRule.Confidence"/>.</item>
///   <item>A stable tie-break by rule id (global rules rank last).</item>
/// </list>
/// </summary>
public sealed class PolicyEngine : IPolicyEngine
{
    private readonly IRecallRuleRepository _rules;

    public PolicyEngine(IRecallRuleRepository rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    public async Task<PolicyResolution> ResolveForTaskAsync(
        string task,
        PolicyContext context,
        CancellationToken cancellationToken = default)
    {
        context ??= PolicyContext.None;

        var keywords = KeywordExtractor.Extract(task);
        if (keywords.Count == 0)
        {
            return Empty();
        }

        var all = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        var keywordSet = new HashSet<string>(keywords, StringComparer.OrdinalIgnoreCase);
        var candidates = all.Where(r => IsRelevant(r, keywordSet)).ToList();

        return Resolve(candidates, context);
    }

    public PolicyResolution Resolve(IReadOnlyList<RecallRule> candidates, PolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        context ??= PolicyContext.None;

        if (candidates.Count == 0)
        {
            return Empty();
        }

        var ignored = new List<RuleVerdict>();
        var candidateIds = candidates.Select(r => r.Id).ToHashSet();

        // 1. Drop rules that are not in force (deprecated, retired, not yet active).
        var eligible = new List<RecallRule>();
        foreach (var rule in candidates)
        {
            if (NotInForceReason(rule) is { } reason)
            {
                ignored.Add(Ignore(rule, reason));
            }
            else
            {
                eligible.Add(rule);
            }
        }

        // 2. Settle explicit supersedes: a rule that supersedes another (in either
        //    direction the data records) wins outright over its target.
        var supersededIds = new HashSet<int>();
        foreach (var rule in eligible)
        {
            // This rule names the rule it replaces.
            if (rule.SupersedesRuleId is { } targetId && candidateIds.Contains(targetId))
            {
                supersededIds.Add(targetId);
            }

            // This rule records that it was itself replaced by a present rule.
            if (rule.SupersededById is { } byId && candidateIds.Contains(byId))
            {
                supersededIds.Add(rule.Id);
            }
        }

        if (supersededIds.Count > 0)
        {
            foreach (var rule in eligible.Where(r => supersededIds.Contains(r.Id)))
            {
                var winner = eligible.FirstOrDefault(r =>
                    r.SupersedesRuleId == rule.Id || rule.SupersededById == r.Id);
                var by = winner is null ? "a newer rule" : $"rule #{winner.Id}";
                ignored.Add(Ignore(rule, $"Superseded by {by}."));
            }

            eligible = eligible.Where(r => !supersededIds.Contains(r.Id)).ToList();
        }

        // 3. Settle direct conflicts within each cluster of opposing rules.
        var conflicts = new List<RuleConflict>();
        var conflictLosers = new HashSet<int>();

        foreach (var cluster in ConflictClusters(eligible))
        {
            var winner = cluster.Rules.Aggregate((best, next) =>
                Prefer(next, best, context).Sign > 0 ? next : best);

            var losers = cluster.Rules.Where(r => r.Id != winner.Id).ToList();
            if (losers.Count == 0)
            {
                continue;
            }

            // The criterion that settled it is the one separating the winner from
            // its closest rival.
            var topRival = losers.Aggregate((best, next) =>
                Prefer(next, best, context).Sign > 0 ? next : best);
            var decided = Prefer(winner, topRival, context).Reason;

            conflicts.Add(new RuleConflict
            {
                Winner = winner,
                Losers = losers,
                Subject = cluster.Subject,
                Reason = decided,
            });

            foreach (var loser in losers)
            {
                conflictLosers.Add(loser.Id);
                var why = Prefer(winner, loser, context).Reason;
                ignored.Add(Ignore(loser,
                    $"Conflicts with rule #{winner.Id} on \"{cluster.Subject}\"; that rule won ({why})."));
            }
        }

        // 4. What survives is effective, ordered best-first.
        var effectiveRules = eligible
            .Where(r => !conflictLosers.Contains(r.Id))
            .ToList();
        effectiveRules.Sort((a, b) => -Prefer(a, b, context).Sign);

        var effective = effectiveRules
            .Select(r => new RuleVerdict
            {
                Rule = r,
                Decision = RuleDecision.Effective,
                Reason = EffectiveReason(r, context),
            })
            .ToList();

        return new PolicyResolution
        {
            Effective = effective,
            Ignored = ignored,
            Conflicts = conflicts,
            Explanation = BuildExplanation(candidates.Count, effective, ignored, conflicts),
        };
    }

    private static PolicyResolution Empty() => new()
    {
        Effective = [],
        Ignored = [],
        Conflicts = [],
        Explanation = "No matching rules.",
    };

    /// <summary>A reason the rule cannot be effective, or null if it is in force.</summary>
    private static string? NotInForceReason(RecallRule rule)
    {
        if (rule.Deprecated)
        {
            return "Deprecated and no longer applied.";
        }

        // The in-force decision uses the shared effective set, so the policy engine,
        // context injection, and search agree; the switch only supplies the message.
        if (RuleStatusSets.Effective.Contains(rule.Status))
        {
            return null;
        }

        return rule.Status switch
        {
            RuleStatus.Superseded => "Superseded by a newer rule.",
            RuleStatus.Archived => "Archived.",
            RuleStatus.Retired => "Retired.",
            _ => $"Not yet active (status: {rule.Status}).",
        };
    }

    private static RuleVerdict Ignore(RecallRule rule, string reason) => new()
    {
        Rule = rule,
        Decision = RuleDecision.Ignored,
        Reason = reason,
    };

    private static string EffectiveReason(RecallRule rule, PolicyContext context) =>
        ScopeRank(rule, context) > 0 && IsProjectMatch(rule, context)
            ? "Effective (project-specific rule)."
            : "Effective.";

    /// <summary>
    /// Groups eligible rules into clusters that transitively conflict, so a single
    /// winner can be chosen per disputed subject.
    /// </summary>
    private static List<(List<RecallRule> Rules, string Subject)> ConflictClusters(List<RecallRule> rules)
    {
        var clusters = new List<(List<RecallRule> Rules, string Subject)>();
        var assigned = new HashSet<int>();

        for (var i = 0; i < rules.Count; i++)
        {
            if (assigned.Contains(rules[i].Id))
            {
                continue;
            }

            var members = new List<RecallRule> { rules[i] };
            var subject = string.Empty;

            // Grow the cluster: any rule conflicting with a current member joins.
            for (var added = true; added;)
            {
                added = false;
                for (var j = 0; j < rules.Count; j++)
                {
                    if (assigned.Contains(rules[j].Id) || members.Any(m => m.Id == rules[j].Id))
                    {
                        continue;
                    }

                    foreach (var member in members)
                    {
                        if (PolarityConflictHeuristic.Conflicts(member, rules[j], out var s))
                        {
                            members.Add(rules[j]);
                            if (subject.Length == 0)
                            {
                                subject = s;
                            }

                            added = true;
                            break;
                        }
                    }
                }
            }

            if (members.Count > 1)
            {
                foreach (var m in members)
                {
                    assigned.Add(m.Id);
                }

                clusters.Add((members, subject));
            }
        }

        return clusters;
    }

    /// <summary>
    /// Orders two rules by the resolution precedence. A positive sign means
    /// <paramref name="a"/> is preferred over <paramref name="b"/>; the reason
    /// names the deciding criterion.
    /// </summary>
    private static (int Sign, string Reason) Prefer(RecallRule a, RecallRule b, PolicyContext context)
    {
        // Explicit supersede relationship, in either recorded direction.
        if (a.SupersedesRuleId == b.Id || b.SupersededById == a.Id)
        {
            return (1, "it explicitly supersedes the other rule");
        }

        if (b.SupersedesRuleId == a.Id || a.SupersededById == b.Id)
        {
            return (-1, "the other rule explicitly supersedes it");
        }

        var scopeA = ScopeRank(a, context);
        var scopeB = ScopeRank(b, context);
        if (scopeA != scopeB)
        {
            return (scopeA > scopeB ? 1 : -1, "it is more project-specific");
        }

        if (a.Priority != b.Priority)
        {
            return (a.Priority > b.Priority ? 1 : -1, "it has higher priority");
        }

        var recency = a.CreatedAt.CompareTo(b.CreatedAt);
        if (recency != 0)
        {
            return (recency > 0 ? 1 : -1, "it is newer");
        }

        if (Math.Abs(a.Confidence - b.Confidence) > 1e-9)
        {
            return (a.Confidence > b.Confidence ? 1 : -1, "it has higher confidence");
        }

        if (a.Id != b.Id)
        {
            // Fully tied: prefer the lower id for a stable, deterministic outcome.
            return (a.Id < b.Id ? 1 : -1, "a stable tie-break (all criteria equal)");
        }

        return (0, "they are identical");
    }

    /// <summary>
    /// Scope precedence relative to the task context: a project-specific match
    /// ranks by its granularity (higher is narrower); everything else (global, or
    /// a rule scoped to a different project) ranks last.
    /// </summary>
    private static int ScopeRank(RecallRule rule, PolicyContext context)
    {
        if (context.ScopeValue is { Length: > 0 })
        {
            return IsProjectMatch(rule, context) ? (int)rule.ScopeLevel : 0;
        }

        // No task scope to compare against: a narrower scope still outranks global.
        return (int)rule.ScopeLevel;
    }

    private static bool IsProjectMatch(RecallRule rule, PolicyContext context) =>
        context.ScopeValue is { Length: > 0 }
        && rule.ScopeLevel != ScopeLevel.Global
        && string.Equals(rule.ScopeValue, context.ScopeValue, StringComparison.OrdinalIgnoreCase)
        && (context.ScopeLevel is null || rule.ScopeLevel == context.ScopeLevel);

    private static bool IsRelevant(RecallRule rule, HashSet<string> keywords)
    {
        var text = string.Join(' ', rule.Trigger, rule.RuleText, rule.Tags, rule.Mistake, rule.TechnicalContext);
        foreach (var token in KeywordExtractor.Extract(text))
        {
            if (keywords.Contains(token))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildExplanation(
        int matched,
        IReadOnlyList<RuleVerdict> effective,
        IReadOnlyList<RuleVerdict> ignored,
        IReadOnlyList<RuleConflict> conflicts)
    {
        var sb = new StringBuilder();
        sb.Append($"Matched {matched} rule(s): {effective.Count} effective, {ignored.Count} ignored.");

        foreach (var conflict in conflicts)
        {
            var losers = string.Join(", ", conflict.Losers.Select(l => $"#{l.Id}"));
            sb.Append($" Conflict on \"{conflict.Subject}\": kept rule #{conflict.Winner.Id} over {losers} because {conflict.Reason}.");
        }

        foreach (var verdict in ignored)
        {
            sb.Append($" Ignored rule #{verdict.Rule.Id}: {verdict.Reason}");
        }

        return sb.ToString();
    }
}
