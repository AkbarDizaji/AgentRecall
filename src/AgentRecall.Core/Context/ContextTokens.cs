using System.Text;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Services;

namespace AgentRecall.Core.Context;

/// <summary>Tokenisation helpers shared by the context scorer.</summary>
internal static class ContextTokens
{
    // Generic words that carry no domain signal when matching rules.
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "to", "of", "in", "on", "for", "and", "or", "it", "is",
        "are", "be", "as", "by", "at", "with", "without", "that", "this", "when",
        "use", "using", "prefer", "always", "never", "avoid", "do", "not", "should",
        "must", "rule", "from", "into", "via", "per", "if", "then", "else",
    };

    // Common file extensions to drop from identifier tokens.
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs", "ts", "tsx", "js", "jsx", "py", "java", "go", "rb", "rs", "cpp", "cc",
        "c", "h", "hpp", "json", "xml", "yml", "yaml", "md", "txt", "sql", "html",
        "css", "scss", "sh", "kt", "swift", "php",
    };

    /// <summary>Salient task keywords (stop-words and generic verbs removed).</summary>
    public static HashSet<string> FromTask(string text) =>
        new(KeywordExtractor.Extract(text), StringComparer.OrdinalIgnoreCase);

    /// <summary>Tokens from file names / code entities, split on camel case and symbols.</summary>
    public static HashSet<string> FromIdentifiers(IEnumerable<string> identifiers)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var identifier in identifiers)
        {
            foreach (var word in SplitWords(identifier))
            {
                if (word.Length >= 2 && !Extensions.Contains(word) && !Stop.Contains(word))
                {
                    tokens.Add(word);
                }
            }
        }

        return tokens;
    }

    /// <summary>All meaningful tokens in a rule's searchable fields.</summary>
    public static HashSet<string> FromRule(RecallRule rule)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in new[] { rule.Trigger, rule.Tags, rule.RuleText, rule.Mistake, rule.TechnicalContext })
        {
            foreach (var word in SplitWords(field))
            {
                if (word.Length >= 2 && !Stop.Contains(word))
                {
                    tokens.Add(word);
                }
            }
        }

        return tokens;
    }

    /// <summary>
    /// Splits text into lower-cased words, breaking on non-alphanumerics and at
    /// camelCase boundaries ("RefundService" → refund, service).
    /// </summary>
    private static IEnumerable<string> SplitWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (!char.IsLetterOrDigit(c))
            {
                if (sb.Length > 0)
                {
                    yield return sb.ToString().ToLowerInvariant();
                    sb.Clear();
                }

                continue;
            }

            // Break a lower/digit → upper transition (camelCase boundary).
            if (sb.Length > 0 && char.IsUpper(c) && !char.IsUpper(sb[^1]))
            {
                yield return sb.ToString().ToLowerInvariant();
                sb.Clear();
            }

            sb.Append(c);
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString().ToLowerInvariant();
        }
    }
}
