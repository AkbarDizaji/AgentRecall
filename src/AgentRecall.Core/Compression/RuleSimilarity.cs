using AgentRecall.Core.Domain;
using AgentRecall.Core.Policy;

namespace AgentRecall.Core.Compression;

/// <summary>A group of rules detected as compressible, with a shared subject.</summary>
internal sealed record RuleCluster(IReadOnlyList<RecallRule> Rules, string Subject, RuleRelationship Relationship);

/// <summary>
/// Groups rules that say the same thing (duplicates), almost the same thing
/// (near-duplicates), or different things about the same subject (overlapping
/// rules / repeated corrections). Deterministic and LLM-free.
///
/// Rules that directly contradict each other (per <see cref="PolarityConflictHeuristic"/>)
/// are never grouped — resolving contradictions is the policy engine's job, not
/// compression's.
/// </summary>
internal static class RuleSimilarity
{
    // Directive verbs, negations, contraction fragments and stop-words removed
    // before comparing what two rules are about.
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "use", "using", "used", "uses", "prefer", "prefers", "preferred", "ensure",
        "always", "do", "does", "not", "never", "avoid", "avoids", "should", "must",
        "the", "a", "an", "please", "no", "stop", "refrain", "from", "to", "in",
        "of", "for", "and", "or", "this", "that", "it", "when", "with", "without",
        "longer", "rather", "than", "instead", "be", "is", "are", "as", "on", "at",
        "don", "dont", "doesn", "didn", "isn", "aren", "wasn", "won", "wont",
        "cant", "cannot", "can", "via", "by", "into", "string", "strings",
    };

    public static IReadOnlyList<RuleCluster> Cluster(IReadOnlyList<RecallRule> rules, CompressionOptions options)
    {
        var clusters = new List<RuleCluster>();

        // Only merge within a single scope — a global rule and a repo-specific one
        // are intentionally distinct.
        foreach (var partition in rules.GroupBy(r => (r.ScopeLevel, r.ScopeValue ?? string.Empty)))
        {
            var items = partition.ToList();
            if (items.Count < 2)
            {
                continue;
            }

            var tokens = items.ToDictionary(r => r.Id, r => Tokenize(r.RuleText));
            var anchors = AnchorTokens(tokens.Values);

            foreach (var component in ConnectedComponents(items, tokens, anchors, options))
            {
                if (component.Count < 2)
                {
                    continue;
                }

                clusters.Add(new RuleCluster(
                    component,
                    Subject(component, tokens),
                    GroupRelationship(component, tokens, options)));
            }
        }

        return clusters;
    }

    private static List<List<RecallRule>> ConnectedComponents(
        List<RecallRule> items,
        Dictionary<int, HashSet<string>> tokens,
        HashSet<string> anchors,
        CompressionOptions options)
    {
        var components = new List<List<RecallRule>>();
        var visited = new HashSet<int>();

        foreach (var seed in items)
        {
            if (!visited.Add(seed.Id))
            {
                continue;
            }

            var component = new List<RecallRule> { seed };
            var queue = new Queue<RecallRule>();
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var other in items)
                {
                    if (visited.Contains(other.Id))
                    {
                        continue;
                    }

                    if (Related(current, other, tokens[current.Id], tokens[other.Id], anchors, options))
                    {
                        visited.Add(other.Id);
                        component.Add(other);
                        queue.Enqueue(other);
                    }
                }
            }

            components.Add(component);
        }

        return components;
    }

    private static bool Related(
        RecallRule a,
        RecallRule b,
        HashSet<string> tokensA,
        HashSet<string> tokensB,
        HashSet<string> anchors,
        CompressionOptions options)
    {
        // Direct contradictions are not compressible.
        if (PolarityConflictHeuristic.Conflicts(a, b))
        {
            return false;
        }

        if (Jaccard(tokensA, tokensB) >= options.OverlapThreshold)
        {
            return true;
        }

        // Otherwise, a shared recurring subject token links them (e.g. "sql").
        return options.UseSharedAnchors && tokensA.Any(t => anchors.Contains(t) && tokensB.Contains(t));
    }

    /// <summary>Tokens that recur across at least two rules — likely subject anchors.</summary>
    private static HashSet<string> AnchorTokens(IEnumerable<HashSet<string>> tokenSets)
    {
        var frequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in tokenSets)
        {
            foreach (var token in set)
            {
                frequency[token] = frequency.GetValueOrDefault(token) + 1;
            }
        }

        return frequency.Where(kv => kv.Value >= 2).Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Subject(IReadOnlyList<RecallRule> group, Dictionary<int, HashSet<string>> tokens)
    {
        var frequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in group)
        {
            foreach (var token in tokens[rule.Id])
            {
                frequency[token] = frequency.GetValueOrDefault(token) + 1;
            }
        }

        // The tokens shared by the most rules describe what the group is about.
        var subject = frequency
            .Where(kv => kv.Value >= 2)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(kv => kv.Key);

        return string.Join(' ', subject);
    }

    private static RuleRelationship GroupRelationship(
        IReadOnlyList<RecallRule> group,
        Dictionary<int, HashSet<string>> tokens,
        CompressionOptions options)
    {
        var strongest = RuleRelationship.Overlapping;

        for (var i = 0; i < group.Count; i++)
        {
            for (var j = i + 1; j < group.Count; j++)
            {
                var similarity = Jaccard(tokens[group[i].Id], tokens[group[j].Id]);
                if (similarity >= 0.999)
                {
                    return RuleRelationship.Duplicate;
                }

                if (similarity >= options.NearDuplicateThreshold)
                {
                    strongest = RuleRelationship.NearDuplicate;
                }
            }
        }

        return strongest;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new string(text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray());

        foreach (var token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 2 && !Noise.Contains(token))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}
