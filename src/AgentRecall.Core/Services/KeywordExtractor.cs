namespace AgentRecall.Core.Services;

/// <summary>
/// Extracts salient keywords from a free-text task description by dropping common
/// English stop-words and generic task verbs. Deterministic; no LLM.
/// </summary>
public static class KeywordExtractor
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Articles, prepositions, conjunctions, pronouns, etc.
        "a", "an", "the", "to", "of", "in", "on", "at", "for", "and", "or", "but",
        "with", "without", "into", "from", "by", "as", "is", "are", "be", "this",
        "that", "these", "those", "it", "its", "my", "our", "your", "their", "we",
        "i", "you", "they", "some", "any", "all", "new", "use", "using",
        // Generic task verbs that add no matching signal.
        "implement", "implementing", "write", "writing", "add", "adding", "create",
        "creating", "build", "building", "make", "making", "fix", "fixing", "update",
        "updating", "refactor", "refactoring", "debug", "debugging", "review",
        "reviewing", "handle", "handling", "work", "working", "do", "doing",
    };

    public static IReadOnlyList<string> Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        // Split on anything that isn't a letter or digit so "code_review" and
        // "auth-token" become separate words.
        var separated = new string(text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray());

        var keywords = new List<string>();
        foreach (var token in separated.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 2 && !StopWords.Contains(token) && !keywords.Contains(token))
            {
                keywords.Add(token);
            }
        }

        return keywords;
    }
}
