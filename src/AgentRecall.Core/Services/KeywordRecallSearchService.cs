using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Search;

namespace AgentRecall.Core.Services;

/// <summary>
/// Keyword-based <see cref="IRecallSearchService"/> with a hybrid-ready ranking
/// pipeline. Relevance is computed from term matches across the rule's text
/// fields; the final score blends relevance with status and confidence.
///
/// Ranking is keyword-only by default: the only registered
/// <see cref="IEmbeddingProvider"/> is a no-op (<c>IsAvailable == false</c>), so no
/// embeddings are computed and no external service is contacted. The pipeline is
/// structured so that a real provider can contribute semantic similarity without
/// changing callers: when one is available, its cosine score is blended into
/// <see cref="ComputeRelevanceAsync"/>. Until then the semantic path is skipped.
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

    // Common function words dropped from queries so matches must land on content
    // words. Without this, a query and an unrelated rule can "match" purely on a
    // word like "in" or "the".
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "but", "if", "then", "else", "of", "to", "in",
        "on", "at", "by", "for", "with", "from", "into", "onto", "as", "is", "are",
        "was", "were", "be", "been", "being", "it", "its", "this", "that", "these",
        "those", "i", "you", "we", "they", "he", "she", "me", "my", "your", "our",
        "their", "do", "does", "did", "done", "can", "could", "should", "would",
        "will", "shall", "may", "might", "must", "what", "which", "who", "whom",
        "how", "when", "where", "why", "not", "no", "yes", "so", "than", "too",
        "very", "just", "about", "up", "out",
    };

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

        // Scope and excluded-status filtering runs in the database so search never loads
        // the full rule table; only term scoring stays in memory.
        var candidates = await _rules.QueryAsync(new RuleQuery
        {
            ScopeLevel = options.ScopeLevel,
            ScopeValue = string.IsNullOrWhiteSpace(options.ScopeValue) ? null : options.ScopeValue,
            ExcludeStatuses = Excluded,
        }, cancellationToken).ConfigureAwait(false);

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

        // Token counts per weighted field. Matching whole tokens (rather than
        // substrings) prevents short terms like "in" from matching inside
        // unrelated words such as "domain" or "instead".
        var fieldTokens = Fields
            .Select(f => (Counts: TokenCounts(f.Select(rule)), f.Weight))
            .ToArray();

        foreach (var term in terms)
        {
            double termScore = 0;
            foreach (var (counts, weight) in fieldTokens)
            {
                if (counts.TryGetValue(term, out var occurrences))
                {
                    termScore += occurrences * weight;
                }
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

    /// <summary>
    /// Distinct content query tokens (length >= 2, stop words removed), in
    /// first-seen order. If the query is made up entirely of stop words, falls
    /// back to the unfiltered tokens so the query still does something.
    /// </summary>
    private static List<string> Tokenize(string text)
    {
        var all = new List<string>();
        var content = new List<string>();
        foreach (var token in EnumerateTokens(text))
        {
            if (!all.Contains(token))
            {
                all.Add(token);
                if (!StopWords.Contains(token))
                {
                    content.Add(token);
                }
            }
        }

        return content.Count > 0 ? content : all;
    }

    /// <summary>Occurrence count per token within a single field's text.</summary>
    private static Dictionary<string, int> TokenCounts(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in EnumerateTokens(text))
        {
            counts[token] = counts.GetValueOrDefault(token) + 1;
        }

        return counts;
    }

    /// <summary>
    /// Splits text on any non-alphanumeric boundary and lowercases each token,
    /// so "console.writeline" yields "console" and "writeline". Single-character
    /// tokens are dropped. Used for both queries and rule fields so they match
    /// on whole words.
    /// </summary>
    private static IEnumerable<string> EnumerateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (builder.Length > 0)
            {
                if (builder.Length >= 2)
                {
                    yield return builder.ToString();
                }

                builder.Clear();
            }
        }

        if (builder.Length >= 2)
        {
            yield return builder.ToString();
        }
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
