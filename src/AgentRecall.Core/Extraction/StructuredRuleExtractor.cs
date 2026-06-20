using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Services;

namespace AgentRecall.Core.Extraction;

/// <summary>The structured fields derived from a piece of feedback.</summary>
public sealed record ExtractedRule
{
    /// <summary>A readable condition for when the rule applies.</summary>
    public string Trigger { get; init; } = string.Empty;

    /// <summary>The canonical guidance (same text as <see cref="Do"/>).</summary>
    public string Rule { get; init; } = string.Empty;

    /// <summary>The positive directive to follow.</summary>
    public string Do { get; init; } = string.Empty;

    /// <summary>The anti-pattern to avoid. Empty when none can be inferred.</summary>
    public string DoNot { get; init; } = string.Empty;

    /// <summary>Why the rule exists. Empty when no rationale is stated. Never the scope.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Where the rule applies, derived from scope.</summary>
    public string AppliesTo { get; init; } = string.Empty;

    public string Tags { get; init; } = string.Empty;
}

/// <summary>
/// Derives the structured fields of a rule from raw feedback using deterministic
/// heuristics (no LLM): it picks the cleanest positive and negative sentences,
/// normalises the trigger into a readable condition, and only fills a reason when
/// the feedback actually states one. It never invents a <c>do_not</c> that merely
/// restates the <c>do</c>.
/// </summary>
public static class StructuredRuleExtractor
{
    private static readonly string[] ReasonMarkers =
        [" because ", " since ", " so that ", " to avoid ", " to prevent ", " otherwise ", " as it "];

    public static ExtractedRule Extract(FeedbackInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var feedback = (input.Feedback ?? string.Empty).Trim();
        var task = (input.Task ?? string.Empty).Trim();
        var fixedOutput = (input.FixedOutput ?? string.Empty).Trim();
        var badOutput = (input.BadOutput ?? string.Empty).Trim();

        // When no task is given and the feedback itself states the condition
        // ("When X, do Y"), prefer the condition as the trigger and derive the
        // action from the remainder — not the whole sentence.
        string trigger;
        string actionSource;
        if (task.Length == 0 && ExtractionHeuristics.TrySplitConditional(feedback, out var condition, out var remainder))
        {
            trigger = ExtractionHeuristics.NormalizeTrigger(condition);
            actionSource = remainder;
        }
        else
        {
            trigger = ExtractionHeuristics.NormalizeTrigger(task.Length > 0 ? task : feedback);
            actionSource = feedback;
        }

        var sentences = ExtractionHeuristics.SplitSentences(actionSource);
        var prescriptive = sentences.FirstOrDefault(ExtractionHeuristics.IsPrescriptive);
        var prohibitive = sentences.FirstOrDefault(ExtractionHeuristics.IsProhibitive);

        // The "do" prefers a clean positive sentence, then a fixed-output sample,
        // then the first sentence as a last resort.
        var doSource = prescriptive
            ?? (fixedOutput.Length > 0
                ? "Use " + fixedOutput
                : sentences.Count > 0 ? sentences[0] : string.Empty);

        // "Use X instead of Y" yields a positive action (X) and the anti-pattern it
        // replaces (Y), so the action never carries the thing to avoid.
        string doText;
        string? avoidFromSubstitution = null;
        if (ExtractionHeuristics.TrySplitSubstitution(doSource, out var action, out var replaced))
        {
            doText = ExtractionHeuristics.NormalizeSentence(action);
            avoidFromSubstitution = replaced;
        }
        else
        {
            doText = ExtractionHeuristics.NormalizeSentence(doSource);
        }

        // The "do not" comes from an explicit prohibition, the bad-output sample, or
        // the replaced anti-pattern — never a restatement of the "do".
        var doNot = prohibitive is not null
            ? ExtractionHeuristics.NormalizeSentence(prohibitive)
            : badOutput.Length > 0
                ? ExtractionHeuristics.NormalizeSentence("Avoid " + badOutput)
                : avoidFromSubstitution is not null
                    ? ExtractionHeuristics.NormalizeSentence("Avoid " + avoidFromSubstitution)
                    : string.Empty;

        if (doNot.Length > 0 && ExtractionHeuristics.Equivalent(doText, doNot))
        {
            doNot = string.Empty;
        }

        return new ExtractedRule
        {
            Trigger = trigger,
            Rule = doText,
            Do = doText,
            DoNot = doNot,
            Reason = ExtractReason(feedback),
            AppliesTo = AppliesTo(input),
            Tags = ResolveTags(input.Tags, task, feedback),
        };
    }

    private static string ExtractReason(string feedback)
    {
        var lower = feedback.ToLowerInvariant();
        foreach (var marker in ReasonMarkers)
        {
            var index = lower.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            // Keep the marker word (minus its leading space) through the sentence end.
            var clause = feedback[(index + 1)..];
            var end = clause.IndexOfAny(['.', '!', '?', '\n', '\r']);
            if (end >= 0)
            {
                clause = clause[..end];
            }

            clause = clause.Trim();
            if (clause.Length > 0)
            {
                return ExtractionHeuristics.NormalizeSentence(clause);
            }
        }

        return string.Empty;
    }

    private static string AppliesTo(FeedbackInput input) =>
        input.ScopeLevel == ScopeLevel.Global || string.IsNullOrWhiteSpace(input.ScopeValue)
            ? input.ScopeLevel.ToString()
            : $"{input.ScopeLevel}:{input.ScopeValue.Trim()}";

    private static string ResolveTags(string? provided, string task, string feedback)
    {
        var normalized = NormalizeTags(provided);
        if (normalized.Length > 0)
        {
            return normalized;
        }

        // Derive a few salient tags when none were supplied, to aid retrieval.
        var derived = KeywordExtractor.Extract($"{task} {feedback}").Take(4);
        return string.Join(",", derived);
    }

    private static string NormalizeTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return string.Empty;
        }

        var normalized = tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(tag => tag.ToLowerInvariant())
            .Distinct();

        return string.Join(",", normalized);
    }
}
