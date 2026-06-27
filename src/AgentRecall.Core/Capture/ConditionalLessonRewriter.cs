namespace AgentRecall.Core.Capture;

/// <summary>
/// A reusable lesson rewritten into conditional, branch-preserving form: the situation
/// it applies to, the action to take, and the mistake to avoid.
/// </summary>
/// <param name="Trigger">When the lesson applies (the condition).</param>
/// <param name="RuleText">What to do (the branch-preserving action).</param>
/// <param name="Mistake">The specific anti-pattern to avoid.</param>
public sealed record ConditionalLesson(string Trigger, string RuleText, string Mistake);

/// <summary>
/// Deterministic rewriter that turns a generic observation of an agent mistake into a
/// conditional lesson — "When X, do Y; avoid Z" — rather than a context-free imperative.
/// A rule elevated by an observed failure should carry the condition under which it
/// matters, so it generalizes without overreaching ("Always merge nested ifs" becomes a
/// branch-preserving rule that only fires when an inner <c>else</c> exists).
///
/// It recognises a small set of known regression shapes and emits their canonical
/// conditional form; for anything it does not recognise it returns <c>null</c> and the
/// extracted rule is used as-is. No LLM, no embeddings — same text, same lesson.
/// </summary>
public static class ConditionalLessonRewriter
{
    /// <summary>
    /// Returns the conditional lesson for a recognised regression shape, or <c>null</c>
    /// when the text matches none.
    /// </summary>
    public static ConditionalLesson? Rewrite(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lower = text.ToLowerInvariant();

        // Flattening nested template conditionals and losing the else branch.
        var nested = lower.Contains("nested", StringComparison.Ordinal) ||
                     lower.Contains("flatten", StringComparison.Ordinal) ||
                     lower.Contains("{{#if", StringComparison.Ordinal);
        var elseBranch = lower.Contains("else", StringComparison.Ordinal) ||
                         lower.Contains("{{else", StringComparison.Ordinal);
        var conditionalForm = lower.Contains("conditional", StringComparison.Ordinal) ||
                              lower.Contains("template", StringComparison.Ordinal) ||
                              lower.Contains("{{#if", StringComparison.Ordinal) ||
                              lower.Contains("(and", StringComparison.Ordinal) ||
                              lower.Contains("if ", StringComparison.Ordinal);

        if (nested && elseBranch && conditionalForm)
        {
            return new ConditionalLesson(
                Trigger: "When flattening nested template conditionals",
                RuleText: "When flattening nested template conditionals, preserve `{{else}}` semantics. " +
                          "If the inner `if` has an `else`, use an equivalent branch-preserving form such as " +
                          "`{{else if (not …)}}` instead of a plain `(and …)` merge.",
                Mistake: "Avoid a plain `(and …)` merge that drops the inner `{{else}}` branch when the inner `if` has an else.");
        }

        // Re-querying an entity the current request already loaded and authorized.
        var requery = lower.Contains("re-query", StringComparison.Ordinal) ||
                      lower.Contains("requery", StringComparison.Ordinal) ||
                      lower.Contains("re query", StringComparison.Ordinal) ||
                      lower.Contains("re-querying", StringComparison.Ordinal);
        var alreadyLoaded = lower.Contains("already loaded", StringComparison.Ordinal) ||
                            lower.Contains("already been loaded", StringComparison.Ordinal) ||
                            lower.Contains("already load", StringComparison.Ordinal);

        if (requery || alreadyLoaded)
        {
            return new ConditionalLesson(
                Trigger: "When the current request already loaded, authorized, and tracked an entity",
                RuleText: "When a controller has already loaded, authorized, and tracked an entity in the same " +
                          "request/DbContext, pass it to downstream logic instead of re-querying the same id, " +
                          "unless the lower layer needs fresh data or must independently enforce authorization/scope.",
                Mistake: "Avoid re-querying an id the current request already loaded and authorized.");
        }

        return null;
    }
}
