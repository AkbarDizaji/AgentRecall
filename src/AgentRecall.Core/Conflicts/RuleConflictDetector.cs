using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Conflicts;

/// <summary>
/// Default <see cref="IRuleConflictDetector"/>. Compares each pair of rules and
/// reports a conflict when they push the same subject in opposing directions:
/// opposite polarity ("use X" vs "do not use X"), a curated antonym pair
/// ("unit tests" vs "integration tests", "Result&lt;T&gt;" vs "exceptions"), one
/// rule's action matching the other's anti-pattern, or the same guidance carried
/// at clashing lifecycle status. Deterministic; no LLM.
/// </summary>
public sealed class RuleConflictDetector : IRuleConflictDetector
{
    // Phrases that flip a rule's guidance negative.
    private static readonly string[] NegationPhrases =
    [
        "do not", "don't", "dont", "should not", "shouldn't", "must not",
        "never", "avoid", "without", "stop using", "no longer",
    ];

    // Curated topics whose two sides are competing approaches, so two rules that
    // each pick a different side disagree even when both are phrased positively.
    private static readonly AntonymTopic[] Antonyms =
    [
        new("test strategy", ["unit"], ["integration"]),
        new("error handling", ["result"], ["exception", "exceptions", "throw", "throwing", "throws"]),
    ];

    // Directive/filler words removed before comparing what two rules are about.
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "use", "using", "used", "uses", "prefer", "prefers", "preferred", "preferring",
        "always", "do", "does", "not", "dont", "don", "never", "avoid", "avoids", "avoiding",
        "should", "must", "the", "a", "an", "please", "no", "stop", "refrain",
        "from", "to", "in", "of", "for", "and", "or", "this", "that", "it", "on", "at", "by",
        "when", "with", "without", "longer", "rather", "than", "instead", "while", "if", "before",
        "after", "once", "during", "writing", "testing", "implementing", "adding", "fixing",
    };

    /// <summary>Minimum subject overlap (Jaccard) for two opposing rules to clash.</summary>
    private const double MinSubjectOverlap = 0.34;

    /// <summary>Subject overlap at which two same-direction rules read as the same guidance.</summary>
    private const double SameGuidanceOverlap = 0.8;

    public IReadOnlyList<RuleConflict> Detect(IReadOnlyList<RecallRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var conflicts = new List<RuleConflict>();
        var ordered = rules.OrderBy(r => r.Id).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                if (TryDetect(ordered[i], ordered[j]) is { } conflict)
                {
                    conflicts.Add(conflict);
                }
            }
        }

        return conflicts;
    }

    private static RuleConflict? TryDetect(RecallRule a, RecallRule b)
    {
        var subjectA = Subject(a.RuleText + " " + a.Trigger);
        var subjectB = Subject(b.RuleText + " " + b.Trigger);
        if (subjectA.Count == 0 || subjectB.Count == 0)
        {
            return null;
        }

        // 1. Opposing sides of a curated topic (e.g. unit vs integration tests).
        foreach (var topic in Antonyms)
        {
            var aLeft = topic.Left.Any(subjectA.Contains);
            var aRight = topic.Right.Any(subjectA.Contains);
            var bLeft = topic.Left.Any(subjectB.Contains);
            var bRight = topic.Right.Any(subjectB.Contains);

            if ((aLeft && bRight && !aRight && !bLeft) || (aRight && bLeft && !aLeft && !bRight))
            {
                return Build(a, b, RuleConflictType.DirectOpposition,
                    $"Conflicting {topic.Name}.",
                    $"The rules choose opposing approaches to {topic.Name}.");
            }
        }

        // 2. One rule recommends exactly what the other names as the anti-pattern.
        if (ActionMatchesAvoid(a, b) || ActionMatchesAvoid(b, a))
        {
            return Build(a, b, RuleConflictType.PreferredVsAvoided,
                "One rule recommends what the other avoids.",
                "One rule's action overlaps the other rule's anti-pattern.");
        }

        var overlap = Jaccard(subjectA, subjectB);
        var oppositePolarity = IsNegative(a.RuleText) != IsNegative(b.RuleText);

        // 3. Opposite polarity on a shared subject ("use X" vs "do not use X").
        if (oppositePolarity && overlap >= MinSubjectOverlap)
        {
            return Build(a, b, RuleConflictType.DirectOpposition,
                "Opposing guidance on the same subject.",
                $"The rules give opposite guidance on a shared subject (overlap {overlap:0.00}).");
        }

        // 4. Near-identical guidance carried at different scope or lifecycle status.
        if (!oppositePolarity && overlap >= SameGuidanceOverlap)
        {
            if (NotInForce(a) != NotInForce(b))
            {
                return Build(a, b, RuleConflictType.StatusConflict,
                    "Same guidance at clashing status.",
                    "The rules are near-identical but disagree on lifecycle status.");
            }

            if (a.ScopeLevel != b.ScopeLevel)
            {
                return Build(a, b, RuleConflictType.BroaderVsSpecific,
                    "Same guidance at different specificity.",
                    "The rules are near-identical but apply at different scope levels.");
            }
        }

        return null;
    }

    private static RuleConflict Build(RecallRule a, RecallRule b, RuleConflictType type, string summary, string reason)
    {
        var min = Math.Min(a.Id, b.Id);
        var max = Math.Max(a.Id, b.Id);
        return new RuleConflict
        {
            ConflictId = $"conflict-{min}-{max}",
            RuleIds = [min, max],
            ConflictType = type,
            Summary = summary,
            DetectedReason = reason,
        };
    }

    private static bool ActionMatchesAvoid(RecallRule recommends, RecallRule avoids)
    {
        if (string.IsNullOrWhiteSpace(avoids.Mistake))
        {
            return false;
        }

        var action = Subject(recommends.RuleText);
        var avoid = Subject(avoids.Mistake);
        return action.Count > 0 && avoid.Count > 0 && Jaccard(action, avoid) >= 0.5;
    }

    private static bool IsNegative(string text)
    {
        var lower = text.ToLowerInvariant();
        return NegationPhrases.Any(p => lower.Contains(p, StringComparison.Ordinal));
    }

    private static bool NotInForce(RecallRule rule) =>
        rule.Deprecated || rule.Status is RuleStatus.Superseded or RuleStatus.Archived or RuleStatus.Retired;

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

    private sealed record AntonymTopic(string Name, string[] Left, string[] Right);
}
