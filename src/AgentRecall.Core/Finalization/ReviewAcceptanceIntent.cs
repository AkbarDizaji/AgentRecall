using System.Text.RegularExpressions;

namespace AgentRecall.Core.Finalization;

/// <summary>
/// Shared detector for "the user accepted external review guidance" intent. Both the
/// Stop-hook capture path (<c>CaptureHook</c>) and the turn finalizer
/// (<see cref="TurnCandidateExtractor"/>) use it, so the two agree on when accepted review
/// guidance should be stored as Active.
///
/// Regex patterns (rather than fixed phrases) tolerate intervening words that a substring
/// list misses — e.g. "apply the reviewer's second comment" or "do exactly what the review
/// says". Callers may combine this with a few extra exact phrases the patterns don't cover
/// (e.g. "the review comment was applied", where the verb trails the noun).
/// </summary>
public static class ReviewAcceptanceIntent
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex[] Patterns =
    [
        // apply/address/accept/… + review/comment/feedback/suggestion
        new(@"\b(apply|address|accept|implement|take)\b.{0,40}\b(the\s+)?(review|comment|feedback|suggestion)s?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout),

        // do/make/fix … as/per/what … review/reviewer/comment
        new(@"\b(do|make|fix)\b.{0,40}\b(as|per|what)\b.{0,40}\b(the\s+)?(review|reviewer|comment)s?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout),

        // per/following/based on … review/comment/feedback/suggestion
        new(@"\b(per|following|based\s+on)\b.{0,20}\b(the\s+)?(review|comment|feedback|suggestion)s?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout),

        // as (suggested in) the review / as the reviewer …
        new(@"\bas\b.{0,20}\b(the\s+)?(review|reviewer)s?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout),
    ];

    /// <summary>True when the text reads as accepting external review guidance.</summary>
    public static bool Matches(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Patterns.Any(p => p.IsMatch(text));
}
