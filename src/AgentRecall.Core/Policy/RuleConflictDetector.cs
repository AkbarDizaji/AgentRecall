using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Policy;

/// <summary>
/// Detects direct conflicts between two rules: guidance that points the same
/// subject in opposite directions, e.g. "Use the repository pattern" versus
/// "Do not use the repository pattern". Heuristic and deterministic; no LLM.
/// </summary>
internal static class RuleConflictDetector
{
    // Phrases that flip a rule's guidance to negative. Multi-word markers are
    // checked against the raw text; single words are matched as whole tokens.
    private static readonly string[] NegationPhrases =
    [
        "do not", "don't", "dont", "should not", "shouldn't", "must not",
        "never", "avoid", "without", "stop using", "no longer",
    ];

    // Directive/filler words stripped before comparing what two rules are about,
    // so polarity words and verbs don't dilute the shared subject.
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "use", "using", "used", "uses", "prefer", "prefers", "preferred",
        "always", "do", "does", "not", "dont", "don", "never", "avoid", "avoids",
        "should", "must", "the", "a", "an", "please", "no", "stop", "refrain",
        "from", "to", "in", "of", "for", "and", "or", "this", "that", "it",
        "when", "with", "without", "longer", "rather", "than", "instead",
    };

    /// <summary>Minimum subject overlap (Jaccard) for two opposing rules to clash.</summary>
    private const double MinSubjectOverlap = 0.5;

    /// <summary>
    /// True when the two rules give opposing guidance on a shared subject.
    /// </summary>
    public static bool Conflicts(RecallRule a, RecallRule b) =>
        Conflicts(a, b, out _);

    public static bool Conflicts(RecallRule a, RecallRule b, out string subject)
    {
        subject = string.Empty;

        if (IsNegative(a.RuleText) == IsNegative(b.RuleText))
        {
            return false; // same polarity — agreement, not a conflict
        }

        var subjectA = Subject(a.RuleText);
        var subjectB = Subject(b.RuleText);
        if (subjectA.Count == 0 || subjectB.Count == 0)
        {
            return false;
        }

        var overlap = Jaccard(subjectA, subjectB);
        if (overlap < MinSubjectOverlap)
        {
            return false;
        }

        var shared = subjectA.Intersect(subjectB, StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.Ordinal);
        subject = string.Join(' ', shared);
        return true;
    }

    private static bool IsNegative(string text)
    {
        var lower = text.ToLowerInvariant();
        foreach (var phrase in NegationPhrases)
        {
            if (lower.Contains(phrase, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The meaningful subject tokens of a rule, with noise removed.</summary>
    private static HashSet<string> Subject(string text)
    {
        var subject = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new string(text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray());

        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 2 && !Noise.Contains(token))
            {
                subject.Add(token);
            }
        }

        return subject;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}
