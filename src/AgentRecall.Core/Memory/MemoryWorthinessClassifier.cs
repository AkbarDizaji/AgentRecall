using System.Text.RegularExpressions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Memory;

/// <summary>
/// Deterministic, rule-based <see cref="IMemoryWorthinessClassifier"/>. It encodes
/// the "lessons, not facts" policy: low-value code facts (a method/property exists,
/// a file path, one service calling another, "use method X") are rejected because
/// they are recoverable from the repository with search, while reusable lessons
/// (cross-layer consistency rules, conventions, bug patterns, reasoned principles)
/// are kept. A code fact that hints at a reusable pattern is flagged for review with
/// a suggested generalized lesson. No LLM and no embeddings — same input, same output.
/// </summary>
public sealed class MemoryWorthinessClassifier : IMemoryWorthinessClassifier
{
    // A candidate that opens with a condition ("when …", "if …") and prescribes a
    // principle reads as a reusable lesson, not a one-off fact.
    private static readonly string[] PrincipleVerbs =
    [
        "ensure", "verify", "avoid", "make sure", "must", "don't", "do not", "never",
        "prefer", "always", "check", "keep", "validate", "confirm", "match",
    ];

    // Phrases that, on their own, mark generalized guidance — typically a
    // cross-layer consistency rule, a convention, or a stated principle.
    private static readonly string[] LessonPhrases =
    [
        "consistent", "consistency", "same definition", "in sync", "across layers",
        "frontend and backend", "backend and frontend", "both sides", "avoid mixing",
        "verify both", "must not", "convention", "as a rule", "in general",
        "stay consistent", "keep them aligned",
    ];

    // A token matching a member access (Foo.Bar) or a call (Method()).
    private static readonly Regex MemberOrCall = new(@"[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_]|\(", RegexOptions.Compiled);

    // A bare PascalCase/interface identifier: at least two uppercase letters and at
    // least one lowercase, so all-caps acronyms ("SQL", "LGTM") are not symbols.
    private static readonly Regex PascalIdentifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    // A filename with a recognised source/config extension.
    private static readonly Regex FileName =
        new(@"\b[\w-]+\.(json|cs|xml|yaml|yml|config|csproj|ts|js|py|txt|ini|toml|md)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public MemoryWorthinessResult Classify(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return new MemoryWorthinessResult(MemoryWorthiness.NotWorthStoring, "Empty candidate.", 1.0);
        }

        var text = candidate.Trim();
        var lower = text.ToLowerInvariant();

        // 1. A generalized lesson wins outright — even if it mentions a symbol.
        if (HasLessonSignal(lower))
        {
            return new MemoryWorthinessResult(
                MemoryWorthiness.WorthStoring,
                "Captures a reusable engineering lesson (conditional, cross-layer, or stated principle).",
                0.9,
                Category: Categorize(lower));
        }

        // 2. A low-value code fact is rejected — unless it hints at a reusable pattern.
        var fact = DetectCodeFact(text, lower);
        if (fact is not null)
        {
            var pattern = DetectGeneralizablePattern(lower);
            if (pattern is not null)
            {
                // The raw detail is a fact, but it reveals a repo convention worth
                // keeping (as the generalized lesson) once reviewed.
                return new MemoryWorthinessResult(
                    MemoryWorthiness.NeedsReview,
                    $"The specific code detail is a fact, but the underlying {pattern.Value.Topic} is reusable.",
                    0.7,
                    pattern.Value.Lesson,
                    RuleCategory.RepositoryConvention);
            }

            return new MemoryWorthinessResult(
                MemoryWorthiness.NotWorthStoring,
                $"Looks like a {fact}, which is recoverable from the repository with search.",
                0.85,
                Category: RuleCategory.CodeFact);
        }

        // 3. Nothing distinctive: keep it as guidance, but with modest confidence.
        return new MemoryWorthinessResult(
            MemoryWorthiness.WorthStoring,
            "No low-value code-fact pattern detected; storing as guidance.",
            0.5,
            Category: Categorize(lower));
    }

    /// <summary>
    /// Splits a store-worthy candidate into a reusable engineering lesson (a
    /// general principle or cross-layer consistency rule that survives refactors)
    /// or a repository convention (conditional guidance about what to use here).
    /// </summary>
    private static RuleCategory Categorize(string lower) =>
        LessonPhrases.Any(p => lower.Contains(p, StringComparison.Ordinal))
            ? RuleCategory.EngineeringLesson
            : RuleCategory.RepositoryConvention;

    private static bool HasLessonSignal(string lower)
    {
        if (LessonPhrases.Any(p => lower.Contains(p, StringComparison.Ordinal)))
        {
            return true;
        }

        // A conditional opener that also prescribes a principle.
        var conditional = lower.StartsWith("when ", StringComparison.Ordinal) || lower.StartsWith("if ", StringComparison.Ordinal);
        return conditional && PrincipleVerbs.Any(v => lower.Contains(v, StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns a human label for the low-value code fact the candidate looks like,
    /// or <c>null</c> if it does not read as one.
    /// </summary>
    private static string? DetectCodeFact(string text, string lower)
    {
        var hasSymbol = text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(IsCodeSymbol);

        // "X has Y", "X exposes Y", "Y exists", "there is a Y" — a member/type
        // existence fact.
        if (hasSymbol && (lower.Contains(" has ", StringComparison.Ordinal) ||
                          lower.Contains(" exists", StringComparison.Ordinal) ||
                          lower.Contains("there is", StringComparison.Ordinal) ||
                          lower.Contains(" contains ", StringComparison.Ordinal) ||
                          lower.Contains(" exposes ", StringComparison.Ordinal) ||
                          lower.Contains(" defines ", StringComparison.Ordinal) ||
                          lower.Contains(" provides ", StringComparison.Ordinal)))
        {
            return "method/property existence fact";
        }

        // "X calls Y", "X invokes Y" — a wiring fact between two components.
        if (hasSymbol && (lower.Contains(" calls ", StringComparison.Ordinal) ||
                          lower.Contains(" invokes ", StringComparison.Ordinal)))
        {
            return "service-call fact";
        }

        // A file path, or a "this config key exists" style fact.
        if (lower.Contains("appsettings", StringComparison.Ordinal) || FileName.IsMatch(text))
        {
            return "file-path fact";
        }

        if ((lower.Contains("config", StringComparison.Ordinal) || lower.Contains("setting", StringComparison.Ordinal)) &&
            (lower.Contains(" key", StringComparison.Ordinal) || lower.Contains(" exists", StringComparison.Ordinal) ||
             lower.Contains(" is in ", StringComparison.Ordinal) || lower.Contains("located", StringComparison.Ordinal)))
        {
            return "config-key fact";
        }

        // "Use <Symbol>" — only a low-value fact when it is a *bare* recommendation
        // (nothing but the symbol) or a symbol-for-symbol substitution. A "use X"
        // with qualifying context ("…in library code") is a reusable convention.
        if (lower.StartsWith("use ", StringComparison.Ordinal))
        {
            var rest = text[4..].TrimStart();
            var parts = rest.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && IsCodeSymbol(parts[0]))
            {
                var remainder = parts.Length > 1 ? parts[1] : string.Empty;
                var substitution = lower.Contains("instead", StringComparison.Ordinal) || lower.Contains("rather than", StringComparison.Ordinal);
                if (substitution || IsEffectivelyEmpty(remainder))
                {
                    return "bare method recommendation";
                }
            }
        }

        return null;
    }

    // Stopwords that carry no qualifying context after a "use <Symbol>" clause.
    private static readonly HashSet<string> RemainderStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "for", "to", "in", "on", "of", "it", "this", "that", "here", "now",
    };

    /// <summary>
    /// True when the remainder of a clause adds no qualifying context — only
    /// punctuation and stopwords, so the clause is just the bare symbol.
    /// </summary>
    private static bool IsEffectivelyEmpty(string remainder)
    {
        var words = remainder
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', ';', ':', '!', '?', ')', '(', '"', '\''))
            .Where(w => w.Length > 0);

        return !words.Any(w => w.Length >= 3 && !RemainderStopwords.Contains(w));
    }

    /// <summary>
    /// When a code fact hints at a reusable engineering pattern, returns the topic
    /// and the generalized lesson to store instead; otherwise <c>null</c>.
    /// </summary>
    private static (string Topic, string Lesson)? DetectGeneralizablePattern(string lower)
    {
        var substitution = lower.Contains("instead", StringComparison.Ordinal) || lower.Contains("rather than", StringComparison.Ordinal);
        var flagLimit = lower.Contains("flag", StringComparison.Ordinal) && lower.Contains("limit", StringComparison.Ordinal);
        var crossLayer = lower.Contains("frontend", StringComparison.Ordinal) || lower.Contains("backend", StringComparison.Ordinal);

        var gate = lower.Contains("feature", StringComparison.Ordinal) || lower.Contains("gate", StringComparison.Ordinal) ||
                   lower.Contains("enabled", StringComparison.Ordinal) || lower.Contains("flag", StringComparison.Ordinal) ||
                   lower.Contains("migrated", StringComparison.Ordinal) || lower.Contains("toggle", StringComparison.Ordinal);

        if (gate && (substitution || flagLimit || crossLayer))
        {
            return ("cross-layer feature-gate consistency issue",
                "When implementing feature gates, use the canonical gate definition and verify backend and frontend conditions match.");
        }

        if ((lower.Contains("auth", StringComparison.Ordinal) || lower.Contains("permission", StringComparison.Ordinal) ||
             lower.Contains("middleware", StringComparison.Ordinal)) && substitution)
        {
            return ("authorization-check completeness issue",
                "When fixing authorization bugs, verify the check is enforced at every layer (middleware and handler), not just one.");
        }

        if (lower.Contains("mock", StringComparison.Ordinal) || lower.Contains("moq", StringComparison.Ordinal) ||
            lower.Contains("matcher", StringComparison.Ordinal))
        {
            return ("test mocking consistency issue",
                "When writing tests with mocks, keep the mocking approach consistent rather than mixing exact instances with matcher-based setups.");
        }

        if (substitution)
        {
            return ("reusable convention behind this choice",
                "Record the reusable reason or convention behind this choice rather than the specific code symbol.");
        }

        return null;
    }

    private static bool IsCodeSymbol(string token)
    {
        var trimmed = token.Trim().TrimEnd('.', ',', ';', ':', '!', '?', ')', '"', '\'');
        if (trimmed.Length < 2)
        {
            return false;
        }

        if (MemberOrCall.IsMatch(trimmed))
        {
            return true;
        }

        if (!PascalIdentifier.IsMatch(trimmed))
        {
            return false;
        }

        var upper = trimmed.Count(char.IsUpper);
        var lower = trimmed.Count(char.IsLower);
        return upper >= 2 && lower >= 1;
    }
}
