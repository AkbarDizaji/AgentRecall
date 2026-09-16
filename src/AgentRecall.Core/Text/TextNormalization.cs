namespace AgentRecall.Core.Text;

/// <summary>
/// The one place rule text is folded down for comparison: lowercased, punctuation
/// turned into separators, whitespace collapsed.
///
/// Every similarity, conflict, dedupe and audit check in the codebase needs the same
/// fold, and each had grown its own copy. Copies drift — one lowercases before
/// splitting, another after; one treats an underscore as a separator, another does
/// not — and two rules then compare equal in the conflict detector but different in
/// the dedupe check. Keeping the fold in one place keeps those verdicts consistent.
/// </summary>
public static class TextNormalization
{
    /// <summary>
    /// The comparison form of <paramref name="text"/>: lowercase words separated by
    /// single spaces, with everything that is not a letter or digit dropped. Empty for
    /// null, blank, or punctuation-only input.
    /// </summary>
    public static string Collapse(string? text) => string.Join(' ', Words(text));

    /// <summary>
    /// The lowercase word tokens of <paramref name="text"/>, split on every run of
    /// non-alphanumeric characters, so <c>code_review</c> and <c>auth-token</c> each
    /// yield two words. Order is preserved and duplicates are kept; callers that want a
    /// set or a noise filter apply their own.
    /// </summary>
    public static IEnumerable<string> Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var separated = new string(text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray());
        foreach (var word in separated.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            yield return word;
        }
    }

    /// <summary>
    /// The meaningful subject tokens of <paramref name="text"/>: <see cref="Words"/>
    /// with single characters and <paramref name="noise"/> words removed, as a
    /// case-insensitive set. This is the shape every Jaccard-style overlap check here
    /// compares; only the noise list differs between them.
    /// </summary>
    public static HashSet<string> SubjectTokens(string? text, IReadOnlySet<string> noise)
    {
        ArgumentNullException.ThrowIfNull(noise);

        var subject = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in Words(text))
        {
            if (word.Length >= 2 && !noise.Contains(word))
            {
                subject.Add(word);
            }
        }

        return subject;
    }
}
