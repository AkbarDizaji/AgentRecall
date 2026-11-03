using System.Text.RegularExpressions;

namespace AgentRecall.Core.Context;

/// <summary>What a <see cref="RuleAnchor"/> pins a rule to.</summary>
public enum AnchorKind
{
    /// <summary>A path pattern, relative to the repository: <c>tests/**</c>, <c>**/*Controller.cs</c>.</summary>
    Path,

    /// <summary>A class or other named symbol: <c>OrderService</c>.</summary>
    Symbol,
}

/// <summary>One place in the code a rule belongs to.</summary>
public readonly record struct RuleAnchor(AnchorKind Kind, string Value)
{
    /// <summary>The stored form, <c>path:…</c> or <c>symbol:…</c>.</summary>
    public override string ToString() => $"{(Kind == AnchorKind.Path ? "path" : "symbol")}:{Value}";
}

/// <summary>
/// Reads, writes, and matches the anchors stored on <see cref="Domain.RecallRule.Anchors"/>.
///
/// A path pattern is matched against the tail of a file's path on a segment boundary, because
/// rules store repository-relative patterns while hooks report absolute paths. A pattern with no
/// wildcard names a file or a whole directory, so <c>src/Billing</c> covers everything under it.
/// A symbol matches a file named after it, a type the file declares, or a mention of it in the
/// task — which is how a ticket that names a class reaches the rules about that class.
/// </summary>
public static partial class RuleAnchors
{
    private const int MaxValueLength = 200;

    /// <summary>The anchors in a stored value; malformed lines are ignored.</summary>
    public static IReadOnlyList<RuleAnchor> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        var anchors = new List<RuleAnchor>();
        foreach (var line in stored.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParse(line, out var anchor) && !anchors.Contains(anchor))
            {
                anchors.Add(anchor);
            }
        }

        return anchors;
    }

    /// <summary>
    /// Reads one anchor written as <c>path:…</c> or <c>symbol:…</c>. A path is normalized to
    /// forward slashes with no leading <c>./</c> or <c>/</c> and no trailing slash; a symbol must
    /// be a single identifier.
    /// </summary>
    public static bool TryParse(string? text, out RuleAnchor anchor)
    {
        anchor = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var separator = text.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var kind = text[..separator].Trim().ToLowerInvariant();
        var value = text[(separator + 1)..].Trim();
        if (value.Length == 0 || value.Length > MaxValueLength)
        {
            return false;
        }

        switch (kind)
        {
            case "path":
                var path = NormalizePattern(value);
                if (path.Length == 0)
                {
                    return false;
                }

                anchor = new RuleAnchor(AnchorKind.Path, path);
                return true;

            case "symbol":
                if (!Identifier().IsMatch(value) || Identifier().Match(value).Length != value.Length)
                {
                    return false;
                }

                anchor = new RuleAnchor(AnchorKind.Symbol, value);
                return true;

            default:
                return false;
        }
    }

    /// <summary>The stored form of <paramref name="anchors"/>: one per line, without repeats.</summary>
    public static string Format(IEnumerable<RuleAnchor> anchors) =>
        string.Join('\n', anchors.Distinct().Select(a => a.ToString()));

    /// <summary>
    /// The anchor that ties a rule to this work, or null when none does. Paths are matched
    /// against <paramref name="fileNames"/>; symbols against those files' names,
    /// <paramref name="entities"/> (e.g. the types a file declares), and the identifiers in
    /// <paramref name="task"/>.
    /// </summary>
    public static RuleAnchor? FirstMatch(
        IReadOnlyList<RuleAnchor> anchors,
        IReadOnlyList<string> fileNames,
        IReadOnlyList<string> entities,
        string? task)
    {
        if (anchors.Count == 0)
        {
            return null;
        }

        HashSet<string>? names = null;
        foreach (var anchor in anchors)
        {
            if (anchor.Kind == AnchorKind.Path)
            {
                if (fileNames.Any(file => PathMatches(anchor.Value, file)))
                {
                    return anchor;
                }

                continue;
            }

            names ??= NamesInScope(fileNames, entities, task);
            if (names.Contains(anchor.Value))
            {
                return anchor;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="filePath"/> falls under <paramref name="pattern"/>. <c>**</c> spans
    /// any number of directories, <c>*</c> and <c>?</c> stay within one name, and comparison
    /// ignores case. The pattern may match from any directory of the path, so a repository-relative
    /// pattern matches an absolute path.
    /// </summary>
    public static bool PathMatches(string pattern, string filePath)
    {
        var patternSegments = Segments(NormalizePattern(pattern));
        var fileSegments = Segments(filePath.Replace('\\', '/'));
        if (patternSegments.Length == 0 || fileSegments.Length == 0)
        {
            return false;
        }

        // A literal pattern names a file or a directory; as a directory it covers what is under it.
        var literal = !pattern.Contains('*', StringComparison.Ordinal) && !pattern.Contains('?', StringComparison.Ordinal);
        for (var start = 0; start < fileSegments.Length; start++)
        {
            var tail = fileSegments.AsSpan(start);
            if (MatchSegments(patternSegments, tail)
                || (literal && tail.Length > patternSegments.Length && MatchSegments(patternSegments, tail[..patternSegments.Length])))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The names of the types and functions <paramref name="code"/> declares, in order and
    /// without repeats. Covers the common declaration keywords across mainstream languages; it is
    /// a cheap scan, not a parser, so it may miss exotic declarations but never invents a name.
    /// </summary>
    public static IReadOnlyList<string> DeclaredSymbols(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return [];
        }

        return Declaration().Matches(code)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static HashSet<string> NamesInScope(
        IReadOnlyList<string> fileNames,
        IReadOnlyList<string> entities,
        string? task)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in fileNames)
        {
            // OrderService.cs and OrderService.test.ts are both about OrderService.
            var name = Path.GetFileName(file.Replace('\\', '/'));
            var dot = name.IndexOf('.', StringComparison.Ordinal);
            names.Add(dot > 0 ? name[..dot] : name);
        }

        names.UnionWith(entities.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()));

        if (!string.IsNullOrWhiteSpace(task))
        {
            foreach (Match match in Identifier().Matches(task))
            {
                names.Add(match.Value);
            }
        }

        return names;
    }

    private static string NormalizePattern(string pattern)
    {
        var path = pattern.Trim().Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return path.Trim('/');
    }

    private static string[] Segments(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool MatchSegments(ReadOnlySpan<string> pattern, ReadOnlySpan<string> path)
    {
        if (pattern.IsEmpty)
        {
            return path.IsEmpty;
        }

        if (pattern[0] == "**")
        {
            // Zero directories, or one more and try again.
            for (var skip = 0; skip <= path.Length; skip++)
            {
                if (MatchSegments(pattern[1..], path[skip..]))
                {
                    return true;
                }
            }

            return false;
        }

        return !path.IsEmpty && SegmentMatches(pattern[0], path[0]) && MatchSegments(pattern[1..], path[1..]);
    }

    private static bool SegmentMatches(string pattern, string segment)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal) && !pattern.Contains('?', StringComparison.Ordinal))
        {
            return string.Equals(pattern, segment, StringComparison.OrdinalIgnoreCase);
        }

        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(segment, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"\b(?:class|interface|record|struct|enum|trait|protocol|object|module|type|def|func|function|fn)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex Declaration();
}
