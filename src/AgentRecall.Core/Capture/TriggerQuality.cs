namespace AgentRecall.Core.Capture;

/// <summary>What is wrong with a rule's trigger, or <see cref="None"/> when it is usable.</summary>
public enum TriggerProblem
{
    /// <summary>The trigger reads as a condition and can match future work.</summary>
    None,

    /// <summary>Empty, or too few words to identify a situation.</summary>
    TooShort,

    /// <summary>
    /// Not a condition at all: a task description, a title, or an instruction. Retrieval matches a
    /// trigger's words against the task at hand, so a trigger that names one past task matches only
    /// the words that task happened to use.
    /// </summary>
    NotConditional,

    /// <summary>The trigger is the opening of its own action, so it carries no separate signal.</summary>
    RestatesTheAction,

    /// <summary>So long it is a paragraph rather than a condition, and dilutes its own keywords.</summary>
    TooLong,
}

/// <summary>
/// Judges whether a rule's trigger can do its job.
///
/// A trigger is not prose to be graded on style — it is the matching surface retrieval ranks
/// against the task at hand, so its quality is whether it can ever match work it applies to. Two
/// failures are worth catching before a rule is stored, because both are invisible afterwards: a
/// trigger written as a task description ("Refactor the Invoice model to use a Money value object")
/// matches on whatever generic words that one task used and then rides along on unrelated turns
/// forever, and a trigger that merely restates its own action adds nothing to match on at all.
///
/// Deliberately deterministic and conservative. It answers "could this ever match?", never "is this
/// the best phrasing?", so it stays a structural floor rather than a matter of taste.
/// </summary>
public static class TriggerQuality
{
    /// <summary>Fewest words that can identify a situation ("when releasing AgentRecall").</summary>
    public const int MinWords = 3;

    /// <summary>Beyond this a trigger is a paragraph, and every extra word dilutes the match.</summary>
    public const int MaxWords = 45;

    /// <summary>
    /// Openers that make a phrase a condition. Kept small on purpose: the repository's own rules all
    /// read "when …", and anything outside this set is far more likely to be a task or a title.
    /// </summary>
    private static readonly string[] ConditionalOpeners =
    [
        "when", "whenever", "if", "while", "before", "after", "during", "on", "any time", "anytime",
    ];

    /// <summary>
    /// Inspects a trigger, optionally against the action it belongs to so a restatement can be
    /// caught. Returns the first problem found, or <see cref="TriggerProblem.None"/>.
    /// </summary>
    public static TriggerProblem Inspect(string? trigger, string? action = null)
    {
        var text = trigger?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return TriggerProblem.TooShort;
        }

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length < MinWords)
        {
            return TriggerProblem.TooShort;
        }

        if (!OpensAsCondition(text))
        {
            return TriggerProblem.NotConditional;
        }

        if (words.Length > MaxWords)
        {
            return TriggerProblem.TooLong;
        }

        return RestatesAction(text, action) ? TriggerProblem.RestatesTheAction : TriggerProblem.None;
    }

    /// <summary>Whether the trigger is usable as it stands.</summary>
    public static bool IsUsable(string? trigger, string? action = null) =>
        Inspect(trigger, action) == TriggerProblem.None;

    /// <summary>A short, human-facing reason, for a downgrade message or an audit line.</summary>
    public static string Describe(TriggerProblem problem) => problem switch
    {
        TriggerProblem.None => "reads as a condition",
        TriggerProblem.TooShort => $"fewer than {MinWords} words, so it identifies no situation",
        TriggerProblem.NotConditional =>
            "not a condition — start it with when/if so it describes a situation, not one past task",
        TriggerProblem.RestatesTheAction => "repeats its own action, so it adds nothing to match on",
        TriggerProblem.TooLong => $"longer than {MaxWords} words, which dilutes the words it matches on",
        _ => "unusable",
    };

    private static bool OpensAsCondition(string text) =>
        ConditionalOpeners.Any(opener =>
            text.StartsWith(opener + " ", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the action simply continues the trigger. Compared on normalized text so a
    /// re-punctuated or re-capitalized restatement still counts.
    /// </summary>
    private static bool RestatesAction(string trigger, string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        var normalizedTrigger = Normalize(trigger);
        var normalizedAction = Normalize(action);

        return normalizedTrigger.Length > 0
            && (normalizedAction.StartsWith(normalizedTrigger, StringComparison.Ordinal)
                || normalizedAction == normalizedTrigger);
    }

    /// <summary>Lowercases, drops punctuation, and collapses whitespace so only the words compare.</summary>
    private static string Normalize(string text)
    {
        var words = text
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();

        return string.Join(
            ' ',
            new string(words).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
