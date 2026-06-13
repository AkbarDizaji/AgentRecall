using AgentRecall.Core.Search;

namespace AgentRecall.Core.Abstractions;

/// <summary>
/// Retrieves rules relevant to a free-text query, ranked by relevance, status
/// and confidence. Superseded and archived rules are never returned.
/// </summary>
public interface IRecallSearchService
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        SearchOptions? options = null,
        CancellationToken cancellationToken = default);
}
