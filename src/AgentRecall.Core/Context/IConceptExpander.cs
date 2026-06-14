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
    private readonly IReadOnlyDictionary<string, string> _termGroup;
    private readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> _activatedBy;

    internal ConceptContext(
        IReadOnlyDictionary<string, string> termGroup,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> activatedBy)
    {
        _termGroup = termGroup;
        _activatedBy = activatedBy;
    }

    /// <summary>The domain groups the seed tokens activated.</summary>
    public IReadOnlyCollection<string> ActivatedGroups => _activatedBy.Keys.ToList();

    /// <summary>
    /// Relates a rule token to an activated concept group. Succeeds only when the
    /// token belongs to a group that the task's seeds actually activated.
    /// </summary>
    public bool TryRelate(string token, out string group, out IReadOnlyCollection<string> viaSeeds)
    {
        if (_termGroup.TryGetValue(token, out var found))
        {
            group = found;
            viaSeeds = _activatedBy[found];
            return true;
        }

        group = string.Empty;
        viaSeeds = [];
        return false;
    }
}
