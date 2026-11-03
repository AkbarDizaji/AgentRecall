using System.Globalization;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Text;

/// <summary>
/// Maps the words a user says onto the English terms rules are written in, so a prompt like
/// "ایونت رو درست کن" is matched as if it also said "event". Matching is on whole words (a
/// phrase matches as a run of whole words), after the same fold rule text gets, plus the
/// letter variants Persian text is commonly typed with.
/// </summary>
public static class Glossary
{
    /// <summary>
    /// The comparison form of a glossary term or of text searched for one: lowercase words
    /// separated by single spaces. Arabic yeh and kaf are folded to their Persian forms and
    /// combining marks dropped, so the same word typed on different keyboards compares equal.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var folded = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            folded.Append(c switch
            {
                'ي' or 'ى' => 'ی',
                'ك' => 'ک',
                _ => c,
            });
        }

        return TextNormalization.Collapse(folded.ToString());
    }

    /// <summary>
    /// The canonical terms of every glossary entry <paramref name="text"/> mentions, in
    /// glossary order and without repeats. Empty when nothing matches.
    /// </summary>
    public static IReadOnlyList<string> Matches(string? text, IReadOnlyCollection<GlossaryTerm> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (terms.Count == 0)
        {
            return [];
        }

        var padded = " " + Normalize(text) + " ";
        if (padded.Length <= 2)
        {
            return [];
        }

        var matched = new List<string>();
        foreach (var term in terms)
        {
            if (term.Term.Length > 0
                && padded.Contains(" " + term.Term + " ", StringComparison.Ordinal)
                && !matched.Contains(term.Canonical, StringComparer.OrdinalIgnoreCase))
            {
                matched.Add(term.Canonical);
            }
        }

        return matched;
    }

    /// <summary>
    /// <paramref name="text"/> with every glossary term it mentions replaced by its canonical
    /// term, so word-based recall matches the English rules — and the user's word does not stay
    /// behind as an extra unmatched term diluting the match. Longer terms are replaced first so
    /// a phrase wins over a word inside it. Returned in normalized form when anything was
    /// replaced, and unchanged when nothing matched.
    /// </summary>
    public static string Translate(string text, IReadOnlyCollection<GlossaryTerm> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (terms.Count == 0 || string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var padded = " " + Normalize(text) + " ";
        var replaced = false;
        foreach (var term in terms.Where(t => t.Term.Length > 0).OrderByDescending(t => t.Term.Length))
        {
            var needle = " " + term.Term + " ";
            if (padded.Contains(needle, StringComparison.Ordinal))
            {
                padded = padded.Replace(needle, " " + term.Canonical + " ", StringComparison.Ordinal);
                replaced = true;
            }
        }

        return replaced ? padded.Trim() : text;
    }
}
