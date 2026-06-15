using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Search;

namespace AgentRecall.Core.Evaluation;

/// <summary>
/// Runs a retrieval evaluation end to end against a rule store: seeds the dataset's
/// corpus, retrieves each scenario through <see cref="IRecallSearchService"/>, and
/// scores the rankings. The caller owns the (ideally throwaway) store behind the
/// repository and search service.
/// </summary>
public static class RetrievalEvaluationHarness
{
    public static async Task<RetrievalEvaluationReport> RunAsync(
        EvaluationDataset dataset,
        IRecallRuleRepository rules,
        IRecallSearchService search,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(search);

        var idToKey = new Dictionary<int, string>();
        foreach (var entry in dataset.Rules)
        {
            var status = Enum.TryParse<RuleStatus>(entry.Status, ignoreCase: true, out var parsed)
                ? parsed
                : RuleStatus.Active;

            var added = await rules.AddAsync(new RecallRule
            {
                Trigger = entry.Trigger,
                RuleText = entry.Rule,
                Mistake = entry.DoNot,
                TechnicalContext = string.Empty,
                Tags = entry.Tags,
                Confidence = status == RuleStatus.Promoted ? 0.9 : 0.6,
                Status = status,
                ScopeLevel = ScopeLevel.Global,
                ScopeValue = string.Empty,
            }, cancellationToken).ConfigureAwait(false);

            idToKey[added.Id] = entry.Key;
        }

        return await RetrievalEvaluator.EvaluateAsync(dataset, async scenario =>
        {
            var options = new SearchOptions { Limit = 5 };
            if (Enum.TryParse<ScopeLevel>(scenario.ScopeLevel, ignoreCase: true, out var level))
            {
                options = options with { ScopeLevel = level };
            }

            if (!string.IsNullOrWhiteSpace(scenario.ScopeValue))
            {
                options = options with { ScopeValue = scenario.ScopeValue };
            }

            var results = await search.SearchAsync(scenario.Query, options, cancellationToken).ConfigureAwait(false);

            return results
                .Where(r => idToKey.ContainsKey(r.Rule.Id))
                .Select(r => idToKey[r.Rule.Id])
                .ToList();
        }).ConfigureAwait(false);
    }
}
