using System.Text.RegularExpressions;

namespace AgentRecall.Core.Extraction;

/// <summary>
/// Deterministic, LLM-free text heuristics shared by the structured extractor and
/// the quality validator: sentence classification, trigger normalisation, and
/// detecting whether two pieces of guidance say the same thing.
/// </summary>
internal static class ExtractionHeuristics
{
    /// <summary>Markers of positive ("do this") guidance.</summary>
    public static readonly string[] Prescriptive =
        ["use ", "prefer ", "always ", "ensure ", "make sure", "should ", "must ", "need to", "favor "];

    /// <summary>Markers of negative ("don't do this") guidance.</summary>
    public static readonly string[] Prohibitive =
        ["don't", "do not", "dont", "never", "avoid", "stop ", "shouldn't", "should not", "no need", "without", "must not", "cannot", "can't"];

    // Words that already open a condition, so a trigger built from them needs no
    // "working on" prefix and no gerund rewrite.
    private static readonly HashSet<string> ConditionalOpeners = new(StringComparer.OrdinalIgnoreCase)
    {
        "when", "whenever", "while", "if", "before", "after", "once", "during",
    };

    // Markers that separate a recommended action from the anti-pattern it replaces.
    private static readonly string[] SubstitutionMarkers = [" instead of ", " rather than "];

    // Leading imperative verbs mapped to their gerund, for readable triggers.
    private static readonly Dictionary<string, string> Gerunds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["write"] = "writing", ["add"] = "adding", ["create"] = "creating", ["build"] = "building",
        ["implement"] = "implementing", ["fix"] = "fixing", ["update"] = "updating", ["refactor"] = "refactoring",
        ["test"] = "testing", ["review"] = "reviewing", ["handle"] = "handling", ["debug"] = "debugging",
        ["use"] = "using", ["validate"] = "validating", ["parse"] = "parsing", ["configure"] = "configuring",
        ["design"] = "designing", ["optimize"] = "optimizing", ["support"] = "supporting", ["render"] = "rendering",
        ["call"] = "calling", ["mock"] = "mocking", ["wire"] = "wiring", ["set"] = "setting",
    };

    // Words carrying no subject signal when comparing two pieces of guidance.
    private static readonly HashSet<string> SubjectNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "use", "using", "prefer", "always", "ensure", "should", "must", "do", "not", "dont", "don",
        "never", "avoid", "the", "a", "an", "to", "of", "in", "for", "and", "or", "this", "that",
        "it", "when", "with", "without", "no", "stop", "make", "sure", "need", "instead", "rather",
        "than", "favor", "like", "via", "be",
    };

    // Split only at real sentence boundaries: terminal punctuation followed by
    // whitespace, or a line break. This leaves code like It.IsAny<T>(), 0.5, and
    // .ConfigureAwait(false) intact instead of shredding it.
    private static readonly Regex SentenceBoundary = new(@"(?<=[.!?])\s+|\r?\n+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static List<string> SplitSentences(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return SentenceBoundary.Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    public static bool ContainsAny(string sentence, string[] markers) =>
        markers.Any(m => sentence.Contains(m, StringComparison.OrdinalIgnoreCase));

    public static bool IsPrescriptive(string sentence) =>
        ContainsAny(sentence, Prescriptive) && !ContainsAny(sentence, Prohibitive);

    public static bool IsProhibitive(string sentence) => ContainsAny(sentence, Prohibitive);

    /// <summary>Whether a piece of guidance reads as a prohibition / negative.</summary>
    public static bool IsNegative(string text)
    {
        var trimmed = text.TrimStart();
        return ContainsAny(text, Prohibitive)
            || trimmed.StartsWith("avoid", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("no ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Trims, strips trailing punctuation, sentence-cases, and adds one period.</summary>
    public static string NormalizeSentence(string sentence)
    {
        var trimmed = sentence.Trim().TrimEnd('.', '!', '?', ' ', ':').Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..] + ".";
    }

    /// <summary>
    /// Turns a raw task into a readable condition, e.g. "write Moq tests for X"
    /// becomes "When writing Moq tests for X" — not "When {raw task}:".
    /// </summary>
    public static string NormalizeTrigger(string? source)
    {
        var text = (source ?? string.Empty).Trim().TrimEnd(':', '.', ' ').Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        var first = space < 0 ? text : text[..space];
        var rest = space < 0 ? string.Empty : text[(space + 1)..];

        // Already a condition ("When implementing X"): keep it, just clean it up.
        if (ConditionalOpeners.Contains(first))
        {
            var cleaned = char.ToUpperInvariant(text[0]) + text[1..];
            return Truncate(cleaned, 90);
        }

        string condition;
        if (Gerunds.TryGetValue(first, out var gerund))
        {
            condition = rest.Length > 0 ? $"{gerund} {rest}" : gerund;
        }
        else if (first.EndsWith("ing", StringComparison.OrdinalIgnoreCase))
        {
            condition = text;
        }
        else
        {
            condition = $"working on {text}";
        }

        return Truncate("When " + condition, 90);
    }

    /// <summary>
    /// When the text opens with a condition and a comma ("When X, do Y"), splits it
    /// into the condition and the remaining action. Returns false otherwise.
    /// </summary>
    public static bool TrySplitConditional(string? text, out string condition, out string action)
    {
        condition = string.Empty;
        action = string.Empty;

        var trimmed = (text ?? string.Empty).TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        var first = space < 0 ? trimmed : trimmed[..space];
        if (!ConditionalOpeners.Contains(first))
        {
            return false;
        }

        var comma = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return false;
        }

        condition = trimmed[..comma].Trim();
        action = trimmed[(comma + 1)..].Trim();
        return condition.Length > 0 && action.Length > 0;
    }

    /// <summary>
    /// Splits "use X instead of Y" / "use X rather than Y" into the recommended
    /// action (X) and the anti-pattern it replaces (Y). Returns false when there is
    /// no substitution marker.
    /// </summary>
    public static bool TrySplitSubstitution(string sentence, out string action, out string avoid)
    {
        action = string.Empty;
        avoid = string.Empty;

        foreach (var marker in SubstitutionMarkers)
        {
            var index = sentence.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var left = sentence[..index].Trim();
            var right = sentence[(index + marker.Length)..].Trim();
            if (left.Length == 0 || right.Length == 0)
            {
                return false;
            }

            action = left;
            avoid = right;
            return true;
        }

        return false;
    }

    /// <summary>Whether two pieces of guidance describe the same subject.</summary>
    public static bool Equivalent(string a, string b)
    {
        var tokensA = SubjectTokens(a);
        var tokensB = SubjectTokens(b);
        if (tokensA.Count == 0 || tokensB.Count == 0)
        {
            return false;
        }

        if (tokensA.SetEquals(tokensB))
        {
            return true;
        }

        var intersection = tokensA.Count(tokensB.Contains);
        var union = tokensA.Count + tokensB.Count - intersection;
        return union > 0 && (double)intersection / union >= 0.8;
    }

    public static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private static HashSet<string> SubjectTokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new string(text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray());

        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 2 && !SubjectNoise.Contains(token))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }
}
