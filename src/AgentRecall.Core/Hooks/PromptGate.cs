namespace AgentRecall.Core.Hooks;

/// <summary>
/// Decides whether a user prompt looks like software-development work and is worth
/// injecting rule context for. Word-aware matching keeps single-word keywords from
/// firing on lookalikes ("test" won't match "latest"); multi-word keywords match
/// as phrases. Deterministic; no LLM.
/// </summary>
public static class PromptGate
{
    /// <summary>The default keyword list; override via configuration.</summary>
    public static readonly string[] DefaultKeywords =
    [
        "implement", "write", "create", "fix", "debug", "refactor", "review", "test",
        "unit test", "integration test", "api", "endpoint", "repository", "service",
        "controller", "moq", "build", "lint",
    ];

    public static bool IsRelevant(string? prompt, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(prompt) || keywords.Count == 0)
        {
            return false;
        }

        var lower = prompt.ToLowerInvariant();
        var words = Tokenize(lower);

        foreach (var keyword in keywords)
        {
            var k = keyword?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(k))
            {
                continue;
            }

            // Phrases (e.g. "unit test") match as substrings; single words match
            // as whole words so "api" doesn't fire on "rapid".
            var matched = k.Contains(' ', StringComparison.Ordinal)
                ? lower.Contains(k, StringComparison.Ordinal)
                : words.Contains(k);

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new string(text.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());
        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(token);
        }

        return words;
    }
}
