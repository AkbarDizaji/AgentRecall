using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Preferences;

/// <summary>
/// The dimension of communication or interaction a preference is about. Drives the
/// normalized wording and is used to detect when a newer preference conflicts with an
/// older one about the same dimension.
/// </summary>
public enum PreferenceDimension
{
    /// <summary>Not a recognised preference.</summary>
    None = 0,

    /// <summary>Answer length / detail: concise vs verbose.</summary>
    Verbosity,

    /// <summary>Response language ("reply in Persian").</summary>
    Language,

    /// <summary>How prompts are delivered ("give me the prompt directly").</summary>
    PromptFormat,

    /// <summary>How often to ask clarifying questions vs assume.</summary>
    Questioning,

    /// <summary>Answering state honestly from AgentRecall status rather than guessing.</summary>
    Honesty,

    /// <summary>Explanation depth / audience level ("explain like I'm junior").</summary>
    ExplanationLevel,

    /// <summary>An explicit preference that does not match a known communication dimension.</summary>
    General,
}

/// <summary>
/// The deterministic result of examining a piece of feedback for an explicit user
/// preference: whether it is one, whether it is unsafe or a do-not-save request, and —
/// when it is a safe preference — the normalized durable rule to store.
/// </summary>
/// <param name="IsPreference">True when the text explicitly states a durable preference.</param>
/// <param name="IsUnsafe">True when the preference conflicts with correctness/honesty and must not be stored as-is.</param>
/// <param name="IsDoNotSave">True when the user explicitly asked not to save/remember this.</param>
/// <param name="Category">The rule category to store under (Communication vs general User preference).</param>
/// <param name="Dimension">Which communication/interaction dimension the preference is about.</param>
/// <param name="NormalizedTrigger">A durable "when …" condition for recall.</param>
/// <param name="NormalizedRule">The durable, bounded guidance (no unsafe absolutes, no insulting wording).</param>
/// <param name="EvidenceSummary">A short account of why this was captured.</param>
/// <param name="Tags">Comma-separated tags including <c>explicit-preference</c> and the dimension.</param>
public sealed record UserPreferenceMatch(
    bool IsPreference,
    bool IsUnsafe,
    bool IsDoNotSave,
    RuleCategory Category,
    PreferenceDimension Dimension,
    string NormalizedTrigger,
    string NormalizedRule,
    string EvidenceSummary,
    string Tags)
{
    /// <summary>A non-match: the text is not an explicit user preference.</summary>
    public static readonly UserPreferenceMatch NoMatch = new(
        false, false, false, RuleCategory.Unknown, PreferenceDimension.None,
        string.Empty, string.Empty, string.Empty, string.Empty);
}

/// <summary>
/// Deterministic detector and normalizer for explicit user preferences, especially
/// communication/style preferences ("answer briefly", "reply in Persian", "give me the
/// prompt directly"). It recognises both English and Persian phrasing, distinguishes a
/// clearly-stated preference from an inferred one, refuses unsafe preferences, and
/// rewrites raw phrasing into durable, bounded guidance. No LLM, no embeddings — the
/// same input always yields the same result.
/// </summary>
public static class UserPreferenceRecognizer
{
    // Explicit "this is a durable preference" markers. A preference is only captured
    // with high confidence when one of these is present — an inferred style from a
    // single message (none of these) is left for the ambiguous path.
    private static readonly string[] ExplicitEnglishSignals =
    [
        "i prefer", "i'd prefer", "i would prefer", "my preference", "from now on",
        "going forward", "in future", "in the future", "always answer", "always respond",
        "always reply", "please always", "keep answers", "keep your answers", "keep it short",
        "keep it simple", "keep it brief", "keep it concise", "don't give long", "do not give long",
        "don't give me long", "explain it like", "explain like i", "when i ask", "when i say",
        "give me the prompt", "just give me the prompt", "i want you to", "i'd like you to",
        "respond in", "answer in", "reply in", "use persian", "use english", "don't ask me",
        "do not ask me", "stop asking me", "tell me if agentrecall", "don't guess",
    ];

    // Persian explicit-preference markers (imperatives and "from now on" phrasing).
    private static readonly string[] ExplicitPersianSignals =
    [
        "از این به بعد", "از الان به بعد", "از این پس", "ترجیح می‌دهم", "ترجیح میدم",
        "همیشه", "کوتاه", "ساده", "مختصر", "پرامپت", "دوست دارم", "بهتره", "نگو",
        "وقتی پرسیدم", "وقتی گفتم", "برای من", "به من", "جواب بده", "جواب‌هاتو", "جواب هاتو",
        "جوابهاتو", "توضیح بده", "مثال بزن", "فارسی", "انگلیسی", "نپرس",
    ];

    // Preferences that conflict with honesty/correctness are never stored as-is.
    private static readonly string[] UnsafeSignals =
    [
        "always say yes", "say yes even", "always agree", "agree with me even", "never disagree",
        "even if wrong", "even if i'm wrong", "even if im wrong", "even if it's wrong",
        "even if its wrong", "even if incorrect", "even when wrong", "just agree", "always tell me i'm right",
        "بله بگو حتی", "همیشه موافقت", "حتی اگر اشتباه", "حتی اگه اشتباه", "همیشه بگو درست",
    ];

    // Explicit "don't persist this" requests (narrow, so "don't say X" stays a preference).
    private static readonly string[] DoNotSaveSignals =
    [
        "don't save this", "do not save this", "don't remember this", "do not remember this",
        "don't store this", "do not store this", "این را ذخیره نکن", "اینو ذخیره نکن", "به خاطر نسپار",
    ];

    /// <summary>
    /// Examines feedback text and returns whether it is an explicit user preference,
    /// along with the normalized durable rule when it is a safe one.
    /// </summary>
    public static UserPreferenceMatch Match(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return UserPreferenceMatch.NoMatch;
        }

        var raw = text.Trim();
        var lower = raw.ToLowerInvariant();

        // An unsafe instruction ("always agree even if I'm wrong") is itself an explicit
        // preference — one we must refuse — so its phrasing counts as an explicit signal.
        var isUnsafe = ContainsAny(lower, UnsafeSignals) || ContainsAny(raw, UnsafeSignals);
        var isExplicit = isUnsafe ||
            ContainsAny(lower, ExplicitEnglishSignals) || ContainsAny(raw, ExplicitPersianSignals);
        if (!isExplicit)
        {
            return UserPreferenceMatch.NoMatch;
        }

        if (ContainsAny(lower, DoNotSaveSignals) || ContainsAny(raw, DoNotSaveSignals))
        {
            return UserPreferenceMatch.NoMatch with { IsPreference = true, IsDoNotSave = true };
        }

        if (isUnsafe)
        {
            return UserPreferenceMatch.NoMatch with { IsPreference = true, IsUnsafe = true, Category = RuleCategory.CommunicationPreference };
        }

        var dimension = Classify(raw, lower);
        return Normalize(dimension, raw, lower);
    }

    private static PreferenceDimension Classify(string raw, string lower)
    {
        // Ordered: the more specific communication dimensions win over generic verbosity.
        if (Contains(lower, "prompt") || Contains(raw, "پرامپت"))
        {
            return PreferenceDimension.PromptFormat;
        }

        if (Contains(lower, "persian") || Contains(lower, "english") || Contains(lower, "language") ||
            Contains(raw, "فارسی") || Contains(raw, "انگلیسی") || Contains(raw, "زبان"))
        {
            return PreferenceDimension.Language;
        }

        if (Contains(lower, "don't ask") || Contains(lower, "do not ask") || Contains(lower, "stop asking") ||
            Contains(lower, "too many question") || Contains(lower, "assumption") || Contains(lower, "assume") ||
            Contains(raw, "نپرس") || Contains(raw, "سوال نپرس") || Contains(raw, "فرض"))
        {
            return PreferenceDimension.Questioning;
        }

        if (Contains(lower, "agentrecall") || Contains(lower, "captured") || Contains(lower, "don't guess") ||
            Contains(lower, "do not guess") || Contains(raw, "حدس نزن"))
        {
            return PreferenceDimension.Honesty;
        }

        if (Contains(lower, "junior") || Contains(lower, "like i'm") || Contains(lower, "like im") ||
            Contains(lower, "like i am") || Contains(raw, "مبتدی"))
        {
            return PreferenceDimension.ExplanationLevel;
        }

        if (Contains(lower, "short") || Contains(lower, "simple") || Contains(lower, "brief") ||
            Contains(lower, "concise") || Contains(lower, "caveman") || Contains(lower, "long") ||
            Contains(raw, "کوتاه") || Contains(raw, "ساده") || Contains(raw, "مختصر"))
        {
            return PreferenceDimension.Verbosity;
        }

        return PreferenceDimension.General;
    }

    private static UserPreferenceMatch Normalize(PreferenceDimension dimension, string raw, string lower)
    {
        return dimension switch
        {
            PreferenceDimension.Verbosity => Communication(
                dimension,
                "When answering or explaining things to this user",
                "Answer briefly and simply first, and prefer concrete examples. Provide more detail only when the user explicitly asks for it.",
                "User explicitly requested concise, simple answers with examples when helpful.",
                "verbosity"),

            PreferenceDimension.Language => Communication(
                dimension,
                "When responding to this user",
                PrefersEnglish(raw, lower)
                    ? "Reply in English by default with this user. Switch language when the user explicitly switches or asks for another language."
                    : "Reply in Persian by default with this user. Switch language when the user explicitly switches or asks for another language.",
                "User explicitly set a default response language.",
                "language"),

            PreferenceDimension.PromptFormat => Communication(
                dimension,
                "When the user asks for a prompt",
                "Provide the copy-paste prompt directly, including the necessary tests and edge cases. Keep the surrounding explanation minimal.",
                "User explicitly requested prompts delivered directly with tests and edge cases.",
                "prompt-format"),

            PreferenceDimension.Questioning => Communication(
                dimension,
                "When a task for this user is underspecified",
                "Avoid asking too many questions; make a reasonable assumption, state it, and proceed. Ask only when a choice is genuinely blocking.",
                "User explicitly asked to make reasonable assumptions instead of asking many questions.",
                "questioning"),

            PreferenceDimension.Honesty => Communication(
                dimension,
                "When the user asks whether AgentRecall captured or saved something",
                "Check AgentRecall status or the turn summary and answer from it; do not guess.",
                "User explicitly asked to confirm capture from AgentRecall status rather than guessing.",
                "honesty"),

            PreferenceDimension.ExplanationLevel => Communication(
                dimension,
                "When explaining concepts to this user",
                "Explain at a junior-friendly level with concrete examples, defining unfamiliar terms as you go.",
                "User explicitly requested junior-friendly explanations with examples.",
                "explanation-level"),

            _ => new UserPreferenceMatch(
                IsPreference: true,
                IsUnsafe: false,
                IsDoNotSave: false,
                Category: RuleCategory.UserPreference,
                Dimension: PreferenceDimension.General,
                NormalizedTrigger: "When interacting with this user",
                NormalizedRule: $"Follow the user's stated preference: {Sanitize(raw)}",
                EvidenceSummary: "User explicitly stated a durable interaction preference.",
                Tags: "user-preference,explicit-preference"),
        };
    }

    private static UserPreferenceMatch Communication(
        PreferenceDimension dimension, string trigger, string rule, string evidence, string dimensionTag) =>
        new(
            IsPreference: true,
            IsUnsafe: false,
            IsDoNotSave: false,
            Category: RuleCategory.CommunicationPreference,
            Dimension: dimension,
            NormalizedTrigger: trigger,
            NormalizedRule: rule,
            EvidenceSummary: evidence,
            Tags: $"communication,style,explicit-preference,{dimensionTag}");

    private static bool PrefersEnglish(string raw, string lower) =>
        (Contains(lower, "english") || Contains(raw, "انگلیسی")) &&
        !Contains(lower, "persian") && !Contains(raw, "فارسی");

    /// <summary>
    /// Removes overbroad absolute openers ("always", "همیشه") so a general preference is
    /// stored as bounded guidance rather than a dangerous absolute, and trims trailing punctuation.
    /// </summary>
    private static string Sanitize(string raw)
    {
        var text = raw.Trim();
        foreach (var opener in new[] { "always ", "Always ", "همیشه " })
        {
            if (text.StartsWith(opener, StringComparison.Ordinal))
            {
                text = text[opener.Length..].TrimStart();
                break;
            }
        }

        text = text.TrimEnd('.', '!', '?', ' ');
        return text.Length == 0 ? raw.Trim() : text;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.Ordinal);

    private static bool ContainsAny(string haystack, string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));
}
