using System.Text.RegularExpressions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Compression;

/// <summary>
/// Builds a canonical rule from a group without an LLM. It picks the strongest,
/// clearest source as the representative and normalises its wording into a single
/// directive (e.g. "Use parameterized SQL." → "Always use parameterized SQL."),
/// then merges the group's triggers, mistakes and tags.
/// </summary>
public sealed partial class DeterministicCanonicalRuleGenerator : ICanonicalRuleGenerator
{
    public CanonicalRule Generate(IReadOnlyList<RecallRule> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("Cannot generate a canonical rule from no sources.", nameof(sources));
        }

        var representative = ChooseRepresentative(sources);

        return new CanonicalRule
        {
            RuleText = Canonicalize(representative.RuleText),
            Trigger = ChooseTrigger(sources, representative),
            Mistake = MergeDistinct(sources.Select(s => s.Mistake)),
            Tags = MergeTags(sources),
            TechnicalContext = MergeDistinct(sources.Select(s => s.TechnicalContext)),
        };
    }

    /// <summary>
    /// Prefer a positively-phrased rule (canonical rules read best as positive
    /// directives), then the most confident, then the most recent, then the most
    /// detailed — finally the lowest id for a deterministic tie-break.
    /// </summary>
    private static RecallRule ChooseRepresentative(IReadOnlyList<RecallRule> sources) =>
        sources
            .OrderBy(s => IsNegative(s.RuleText) ? 1 : 0)
            .ThenByDescending(s => s.Confidence)
            .ThenByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.RuleText.Length)
            .ThenBy(s => s.Id)
            .First();

    private static string ChooseTrigger(IReadOnlyList<RecallRule> sources, RecallRule representative)
    {
        if (!string.IsNullOrWhiteSpace(representative.Trigger))
        {
            return representative.Trigger.Trim();
        }

        return sources.Select(s => s.Trigger).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim()
            ?? string.Empty;
    }

    /// <summary>
    /// Normalises a single rule's text into a canonical directive: a strong leading
    /// adverb, sentence case, and exactly one trailing period.
    /// </summary>
    public static string Canonicalize(string raw)
    {
        var text = Whitespace().Replace(raw ?? string.Empty, " ").Trim().TrimEnd('.').Trim();
        if (text.Length == 0)
        {
            return text;
        }

        var lower = text.ToLowerInvariant();

        string result;
        if (lower.StartsWith("always ", StringComparison.Ordinal) ||
            lower.StartsWith("never ", StringComparison.Ordinal))
        {
            result = text;
        }
        else if (StartsWithWord(lower, "use") || StartsWithWord(lower, "prefer") ||
                 StartsWithWord(lower, "ensure"))
        {
            // Positive directive → "Always <verb> ...".
            result = "Always " + LowerFirst(text);
        }
        else if (StartsWithWord(lower, "avoid"))
        {
            result = text; // already a clear prohibition
        }
        else if (TryStripLeading(text, out var rest, "do not", "don't", "dont", "never"))
        {
            result = "Never " + LowerFirst(rest);
        }
        else
        {
            result = IsNegative(lower) ? text : "Always " + LowerFirst(text);
        }

        return Capitalize(result.Trim()) + ".";
    }

    private static string MergeTags(IReadOnlyList<RecallRule> sources)
    {
        var tags = sources
            .SelectMany(s => (s.Tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.Ordinal);

        return string.Join(",", tags);
    }

    private static string MergeDistinct(IEnumerable<string> values)
    {
        var parts = values
            .Select(v => v?.Trim() ?? string.Empty)
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join("; ", parts);
    }

    private static bool TryStripLeading(string text, out string rest, params string[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                text.Length > prefix.Length &&
                char.IsWhiteSpace(text[prefix.Length]))
            {
                rest = text[(prefix.Length + 1)..].Trim();
                return true;
            }
        }

        rest = text;
        return false;
    }

    private static bool StartsWithWord(string lowerText, string word) =>
        lowerText.StartsWith(word + " ", StringComparison.Ordinal);

    private static bool IsNegative(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("do not", StringComparison.Ordinal)
            || lower.Contains("don't", StringComparison.Ordinal)
            || lower.Contains("dont ", StringComparison.Ordinal)
            || StartsWithWord(lower, "never")
            || StartsWithWord(lower, "avoid")
            || lower.Contains("should not", StringComparison.Ordinal)
            || lower.Contains("must not", StringComparison.Ordinal);
    }

    private static string LowerFirst(string text) =>
        text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
