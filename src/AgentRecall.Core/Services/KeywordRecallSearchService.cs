using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Search;

namespace AgentRecall.Core.Services;

/// <summary>
/// Keyword-based <see cref="IRecallSearchService"/> with a hybrid-ready ranking
/// pipeline. Relevance is computed from term matches across the rule's text
/// fields; the final score blends relevance with status and confidence.
///
/// The pipeline is structured so that a future <see cref="IEmbeddingProvider"/>
/// can contribute semantic similarity without changing callers: when one is
/// available, its score is blended into <see cref="ComputeRelevanceAsync"/>.
/// </summary>
public sealed class KeywordRecallSearchService : IRecallSearchService
{
    // Field weights for keyword relevance (more discriminating fields weigh more).
    private static readonly (Func<RecallRule, string> Select, double Weight)[] Fields =
    [
        (r => r.Trigger, 3.0),
        (r => r.Tags, 3.0),
        (r => r.RuleText, 2.0),
        (r => r.Mistake, 1.5),
        (r => r.TechnicalContext, 1.0),
    ];

    // Statuses never surfaced by search.
    private static readonly HashSet<RuleStatus> Excluded = [RuleStatus.Superseded, RuleStatus.Archived];

    // Composite-score weights.
    private const double RelevanceWeight = 1.0;
    private const double StatusWeight = 0.3;
    private const double ConfidenceWeight = 0.2;

    // Relevance blend between keyword and semantic similarity (1.0 = keyword-only).
    private const double KeywordBlend = 0.6;

    private readonly IRecallRuleRepository _rules;
    private readonly IEmbeddingProvider _embeddings;

    public KeywordRecallSearchService(IRecallRuleRepository rules, IEmbeddingProvider embeddings)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        SearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SearchOptions();

        var terms = Tokenize(query);
        if (terms.Count == 0)
        {
            return [];
        }

        var candidates = (await _rules.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(r => !Excluded.Contains(r.Status))
            .Where(r => MatchesScope(r, options))
            .ToList();

        // Optional semantic vector for the query (skipped when no provider).
        float[]? queryVector = _embeddings.IsAvailable
            ? await _embeddings.EmbedAsync(query, cancellationToken).ConfigureAwait(false)
            : null;

        var results = new List<SearchResult>();
        foreach (var rule in candidates)
        {
            var relevance = await ComputeRelevanceAsync(rule, terms, queryVector, cancellationToken)
                .ConfigureAwait(false);
            if (relevance < options.MinRelevance)
            {
                continue;
            }

            var score =
                relevance * RelevanceWeight +
                StatusScore(rule.Status) * StatusWeight +
                Math.Clamp(rule.Confidence, 0.0, 1.0) * ConfidenceWeight;

            results.Add(new SearchResult { Rule = rule, Score = score, Relevance = relevance });
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Rule.Confidence)
            .ThenByDescending(r => r.Rule.UpdatedAt)
            .Take(options.Limit)
            .ToList();
    }

    /// <summary>
    /// Relevance of a rule to the query terms, 0.0–1.0. Keyword coverage is the
    /// base; when an embedding provider is available its cosine similarity is
    /// blended in (hybrid search). No-op semantic path when unavailable.
    /// </summary>
    private async Task<double> ComputeRelevanceAsync(
        RecallRule rule,
        IReadOnlyList<string> terms,
        float[]? queryVector,
        CancellationToken cancellationToken)
    {
        var keyword = KeywordRelevance(rule, terms);

        if (queryVector is null || !_embeddings.IsAvailable)
        {
            return keyword;
        }

        var ruleVector = await _embeddings
            .EmbedAsync(BuildSearchableText(rule), cancellationToken)
            .ConfigureAwait(false);
        var semantic = CosineSimilarity(queryVector, ruleVector);

        return KeywordBlend * keyword + (1.0 - KeywordBlend) * semantic;
    }

    private static double KeywordRelevance(RecallRule rule, IReadOnlyList<string> terms)
    {
        var matchedTerms = 0;
        double weighted = 0;

        foreach (var term in terms)
        {
            double termScore = 0;
            foreach (var (select, weight) in Fields)
            {
                var occurrences = CountOccurrences(select(rule).ToLowerInvariant(), term);
                termScore += occurrences * weight;
            }

            if (termScore > 0)
            {
                matchedTerms++;
                weighted += termScore;
            }
        }

        if (matchedTerms == 0)
        {
            return 0;
        }

        // Coverage dominates; a small density bonus breaks ties between rules
        // that match the same fraction of terms.
        var coverage = (double)matchedTerms / terms.Count;
        var densityBonus = Math.Min(weighted, 20.0) / 20.0 * 0.1;
        return Math.Min(1.0, coverage + densityBonus);
    }

    private static bool MatchesScope(RecallRule rule, SearchOptions options)
    {
        if (options.ScopeLevel is { } level && rule.ScopeLevel != level)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.ScopeValue) &&
            !string.Equals(rule.ScopeValue, options.ScopeValue, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>Ranking weight per status; higher surfaces first.</summary>
    private static double StatusScore(RuleStatus status) => status switch
    {
        RuleStatus.Promoted => 1.0,
        RuleStatus.Active => 0.85,
        RuleStatus.Pending => 0.5,
        RuleStatus.Draft => 0.3,
        RuleStatus.Retired => 0.1,
        _ => 0.0,
    };

    private static string BuildSearchableText(RecallRule rule) =>
        string.Join(' ', Fields.Select(f => f.Select(rule)));

    private static List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var tokens = new List<string>();
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (token.Length >= 2 && !tokens.Contains(token))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (haystack.Length == 0 || needle.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0 || normB == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
