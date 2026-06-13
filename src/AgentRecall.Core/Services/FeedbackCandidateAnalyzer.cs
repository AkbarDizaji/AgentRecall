using AgentRecall.Core.Abstractions;

namespace AgentRecall.Core.Services;

/// <summary>
/// Heuristic <see cref="IFeedbackCandidateAnalyzer"/>. It treats imperative,
/// corrective statements as candidates and proposes a rule by preferring a
/// prescriptive ("use X") sentence over a prohibitive ("don't X") one.
/// </summary>
public sealed class FeedbackCandidateAnalyzer : IFeedbackCandidateAnalyzer
{
    // Positive guidance ("do this").
    private static readonly string[] Prescriptive =
    [
        "use ", "prefer ", "always ", "make sure", "ensure ", "should ", "must ",
        "need to", "instead", "rather than",
    ];

    // Negative guidance ("don't do this").
    private static readonly string[] Prohibitive =
    [
        "don't", "do not", "dont", "never", "avoid", "stop ", "shouldn't",
        "should not", "no need", "don't use", "do not use",
    ];

    public FeedbackCandidate Analyze(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new FeedbackCandidate(false, string.Empty);
        }

        var sentences = SplitSentences(message);
        if (sentences.Count == 0)
        {
            return new FeedbackCandidate(false, string.Empty);
        }

        // A purely prescriptive sentence ("use X") is the cleanest rule — but it
        // must not itself be a prohibition ("don't use X"). Fall back to a
        // prohibitive sentence when there's no clean positive one.
        var prescriptive = sentences.FirstOrDefault(s => ContainsAny(s, Prescriptive) && !ContainsAny(s, Prohibitive));
        var prohibitive = sentences.FirstOrDefault(s => ContainsAny(s, Prohibitive));

        if (prescriptive is null && prohibitive is null)
        {
            return new FeedbackCandidate(false, string.Empty);
        }

        var rule = Normalize(prescriptive ?? prohibitive!);
        return new FeedbackCandidate(true, rule);
    }

    private static List<string> SplitSentences(string message) =>
        message
            .Split(['.', '!', '?', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static bool ContainsAny(string sentence, string[] markers) =>
        markers.Any(m => sentence.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string sentence)
    {
        var trimmed = sentence.Trim().TrimEnd('.', '!', '?').Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var capitalized = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        return capitalized + ".";
    }
}
