namespace AgentRecall.Core.Context;

/// <summary>
/// Bridges a task's vocabulary to related domain concepts, so a rule can match a
/// task even when they share no literal words (e.g. "refund" relates to "money").
/// </summary>
public interface IConceptExpander
{
    /// <summary>Builds the set of concepts activated by the given seed tokens.</summary>
    ConceptContext Build(IEnumerable<string> seedTokens);
}

/// <summary>
/// The concepts activated for a request: which domain groups the seeds touched
/// and which terms belong to those groups. Lets a scorer relate a rule's words
/// back to the task's words.
/// </summary>
public sealed class ConceptContext
{
    private readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> _termGroups;
    private readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> _activatedBy;

    internal ConceptContext(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> termGroups,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> activatedBy)
    {
        _termGroups = termGroups;
        _activatedBy = activatedBy;
    }

    /// <summary>The domain groups the seed tokens activated.</summary>
    public IReadOnlyCollection<string> ActivatedGroups => _activatedBy.Keys.ToList();

    /// <summary>
    /// Relates a rule token to every activated concept group it belongs to. A token that is a
    /// member of more than one activated group (e.g. it names a concept shared across domains)
    /// relates to all of them, not just one — dropping the others would silently discard a real
    /// semantic match depending on activation order.
    /// </summary>
    public IReadOnlyCollection<(string Group, IReadOnlyCollection<string> ViaSeeds)> RelateAll(string token)
    {
        if (!_termGroups.TryGetValue(token, out var groups))
        {
            return [];
        }

        return groups.Select(group => (group, _activatedBy[group])).ToList();
    }
}
