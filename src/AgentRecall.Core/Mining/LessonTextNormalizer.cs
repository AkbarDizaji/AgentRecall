using System.Text.RegularExpressions;

namespace AgentRecall.Core.Mining;

/// <summary>
/// Deterministic, LLM-free normalization that collapses differently-worded but
/// equivalent signals onto a single key so repeats cluster together. It lowercases,
/// canonicalizes correction verbs, drops filler/politeness words, and strips
/// sentence punctuation while preserving code tokens like <c>Result&lt;T&gt;</c> and
/// <c>It.IsAny&lt;T&gt;()</c>. Same input → same key, always.
/// </summary>
public static class LessonTextNormalizer
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // Negative directives → "avoid"; longer phrases first so they win.
    private static readonly string[] NegativeVerbs =
    [
        "do not use", "don't use", "dont use", "never use", "should not use", "shouldn't use", "avoid using",
    ];

    // Positive directives → "use"; longer phrases first.
    private static readonly string[] PositiveVerbs =
    [
        "make sure to use", "you should use", "remember to use", "always use",
        "prefer to use", "prefer using", "should use", "prefer",
    ];

    // Politeness/filler removed entirely (whole words/phrases). Longer first.
    private static readonly string[] Fillers =
    [
        "can you", "could you", "would you", "please", "kindly", "just", "maybe", "here", "now",
    ];

    // Edge punctuation stripped from each token; parens/angle brackets are kept so
    // code tokens (It.IsAny<T>(), Result<T>) survive.
    private static readonly char[] EdgePunctuation = ['.', ',', ';', ':', '!', '?', '"', '\'', '`'];

    /// <summary>
    /// Produces the deterministic clustering key for a raw signal. Returns an empty
    /// string when the signal carries no content words.
    /// </summary>
    public static string NormalizeKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Wrap in spaces so phrase replacements can match on whole-word boundaries.
        var s = " " + Whitespace.Replace(text.ToLowerInvariant().Trim(), " ") + " ";

        foreach (var phrase in NegativeVerbs)
        {
            s = s.Replace($" {phrase} ", " avoid ", StringComparison.Ordinal);
        }

        foreach (var phrase in PositiveVerbs)
        {
            s = s.Replace($" {phrase} ", " use ", StringComparison.Ordinal);
        }

        foreach (var filler in Fillers)
        {
            // Loop so repeated/adjacent fillers all go ("please please use" → "use").
            while (s.Contains($" {filler} ", StringComparison.Ordinal))
            {
                s = s.Replace($" {filler} ", " ", StringComparison.Ordinal);
            }
        }

        var tokens = Whitespace.Replace(s, " ").Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim(EdgePunctuation))
            .Where(t => t.Length > 0);

        return string.Join(' ', tokens);
    }
}
