namespace AgentRecall.Core.Services;

/// <summary>The kind of log being imported.</summary>
public enum LogKind
{
    Build,
    Test,
    Lint,
}

/// <summary>
/// Extracts failure lines from a log. Intentionally simple: it scans for
/// kind-specific keywords rather than parsing any particular tool's format.
/// </summary>
public static class FailureLogParser
{
    public static IReadOnlyList<string> Parse(LogKind kind, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var keywords = KeywordsFor(kind);

        return lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .Where(line => keywords.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static string[] KeywordsFor(LogKind kind) => kind switch
    {
        LogKind.Build => ["error"],
        LogKind.Test => ["fail"],
        LogKind.Lint => ["error", "warning"],
        _ => [],
    };
}
