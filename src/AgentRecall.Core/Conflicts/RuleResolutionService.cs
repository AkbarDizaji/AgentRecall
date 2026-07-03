using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Conflicts;

/// <summary>
/// Default <see cref="IRuleResolutionService"/>. Scores each rule and picks the
/// highest, where:
/// <code>
/// score = 0.35·scope specificity
///       + 0.25·confidence
///       + 0.20·status weight
///       + 0.10·recency
///       + 0.10·trigger specificity
/// </code>
/// Superseded and archived rules score zero on status and are never selected.
/// Scope specificity is absolute (File/Directory &gt; Repository &gt; Language &gt;
/// Global); recency and trigger specificity are relative within the conflict set,
/// so the result is fully deterministic from the rules alone — no clock, no LLM.
/// </summary>
public sealed class RuleResolutionService : IRuleResolutionService
{
    private const double ScopeWeight = 0.35;
    private const double ConfidenceWeight = 0.25;
    private const double StatusWeight = 0.20;
    private const double RecencyWeight = 0.10;
    private const double TriggerWeight = 0.10;

    // A locally learned rule wins a tie against a built-in seed rule: a repository
    // convention or lesson should beat generic starter guidance when they conflict.
    private const double SourceWeight = 0.15;

    public RuleResolution Resolve(IReadOnlyList<RecallRule> conflictingRules)
    {
        ArgumentNullException.ThrowIfNull(conflictingRules);
        if (conflictingRules.Count == 0)
        {
            throw new ArgumentException("At least one rule is required to resolve.", nameof(conflictingRules));
        }

        var ticks = conflictingRules.Select(Recency).ToList();
        var tickMin = ticks.Min();
        var tickMax = ticks.Max();

        var triggerSizes = conflictingRules.Select(r => TriggerSpecificity(r.Trigger)).ToList();
        var triggerMin = triggerSizes.Min();
        var triggerMax = triggerSizes.Max();

        var scores = conflictingRules
            .Select(rule => Score(rule, tickMin, tickMax, triggerMin, triggerMax))
            .OrderByDescending(s => s.Total)
            .ThenByDescending(s => s.ScopeSpecificity)
            .ThenByDescending(s => s.Confidence)
            .ThenBy(s => s.RuleId)
            .ToList();

        var byId = conflictingRules.ToDictionary(r => r.Id);

        // Superseded/archived rules must never win; prefer an in-force rule.
        var winnerScore = scores.FirstOrDefault(s => !NotInForce(byId[s.RuleId])) ?? scores[0];
        var winner = byId[winnerScore.RuleId];

        var runnerUp = scores.FirstOrDefault(s => s.RuleId != winnerScore.RuleId);
        var explanation = Explain(winner, winnerScore, runnerUp is null ? null : byId[runnerUp.RuleId], runnerUp);

        var ignored = conflictingRules
            .Where(r => r.Id != winner.Id)
            .Select(r => r.Id)
            .OrderBy(id => id)
            .ToList();

        return new RuleResolution
        {
            SelectedRuleId = winner.Id,
            IgnoredRuleIds = ignored,
            ScoreBreakdown = scores,
            Explanation = explanation,
            Confidence = DecisionConfidence(winnerScore, runnerUp),
        };
    }

    private static RuleScore Score(RecallRule rule, long tickMin, long tickMax, int triggerMin, int triggerMax)
    {
        var scope = (double)(int)rule.ScopeLevel / (int)ScopeLevel.File; // Global 0 … File 1
        var confidence = Math.Clamp(rule.Confidence, 0.0, 1.0);
        var status = StatusScore(rule);
        var recency = Normalize(Recency(rule), tickMin, tickMax);
        var trigger = Normalize(TriggerSpecificity(rule.Trigger), triggerMin, triggerMax);
        var source = rule.Source == RuleSource.BuiltInSeed ? 0.0 : 1.0;

        var total =
            ScopeWeight * scope +
            ConfidenceWeight * confidence +
            StatusWeight * status +
            RecencyWeight * recency +
            TriggerWeight * trigger +
            SourceWeight * source;

        return new RuleScore
        {
            RuleId = rule.Id,
            Total = Math.Round(total, 4),
            ScopeSpecificity = Math.Round(scope, 4),
            Confidence = Math.Round(confidence, 4),
            StatusWeight = Math.Round(status, 4),
            Recency = Math.Round(recency, 4),
            TriggerSpecificity = Math.Round(trigger, 4),
        };
    }

    private static double StatusScore(RecallRule rule)
    {
        if (rule.Deprecated)
        {
            return 0.0;
        }

        return rule.Status switch
        {
            RuleStatus.Promoted => 1.0,
            RuleStatus.Active => 0.6,
            RuleStatus.Pending => 0.3,
            RuleStatus.Draft => 0.2,
            _ => 0.0, // Superseded, Archived, Retired — never win.
        };
    }

    private static bool NotInForce(RecallRule rule) =>
        rule.Deprecated || rule.Status is RuleStatus.Superseded or RuleStatus.Archived or RuleStatus.Retired;

    private static long Recency(RecallRule rule) =>
        (rule.LastUsedAt ?? rule.UpdatedAt).UtcTicks;

    /// <summary>Trigger specificity proxy: the count of content tokens in the trigger.</summary>
    private static int TriggerSpecificity(string? trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return 0;
        }

        var normalized = new string(trigger.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray());
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(t => t.Length >= 2 && !TriggerNoise.Contains(t));
    }

    private static readonly HashSet<string> TriggerNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "when", "whenever", "while", "if", "before", "after", "once", "during",
        "the", "a", "an", "to", "of", "in", "on", "for", "and", "or", "with",
    };

    private static double Normalize(double value, double min, double max) =>
        max <= min ? 1.0 : (value - min) / (max - min);

    private static IReadOnlyList<string> Explain(
        RecallRule winner,
        RuleScore winnerScore,
        RecallRule? runnerUp,
        RuleScore? runnerUpScore)
    {
        if (runnerUp is null || runnerUpScore is null)
        {
            return ["Only eligible rule for this conflict."];
        }

        var reasons = new List<string>();

        if (winnerScore.ScopeSpecificity > runnerUpScore.ScopeSpecificity)
        {
            reasons.Add($"More specific scope ({winner.ScopeLevel} over {runnerUp.ScopeLevel})");
        }

        if (winnerScore.Confidence > runnerUpScore.Confidence)
        {
            reasons.Add($"Higher confidence: {winnerScore.Confidence:0.00} vs {runnerUpScore.Confidence:0.00}");
        }

        if (winnerScore.StatusWeight > runnerUpScore.StatusWeight)
        {
            reasons.Add($"{winner.Status} status");
        }

        if (winnerScore.TriggerSpecificity > runnerUpScore.TriggerSpecificity)
        {
            reasons.Add("More specific trigger");
        }

        if (winnerScore.Recency > runnerUpScore.Recency)
        {
            reasons.Add("More recently updated");
        }

        if (reasons.Count == 0)
        {
            reasons.Add("Higher overall match score");
        }

        return reasons;
    }

    private static double DecisionConfidence(RuleScore winner, RuleScore? runnerUp)
    {
        if (runnerUp is null)
        {
            return 1.0;
        }

        var sum = winner.Total + runnerUp.Total;
        if (sum <= 0)
        {
            return 0.5;
        }

        return Math.Round(Math.Clamp(winner.Total / sum, 0.5, 1.0), 2);
    }
}
