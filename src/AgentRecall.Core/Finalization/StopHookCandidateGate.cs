namespace AgentRecall.Core.Finalization;

/// <summary>
/// Why a Stop-hook capture candidate was not stored. Drives the structured skip reason
/// surfaced by capture-status, the turn summary, and the activity log, and the grouping
/// used by <c>cleanup pending-noise</c>.
/// </summary>
public enum CaptureSkipReason
{
    /// <summary>Not a skip.</summary>
    None = 0,

    /// <summary>The turn (or candidate) carried an explicit user do-not-save instruction.</summary>
    ExplicitDoNotSave,

    /// <summary>The candidate reads as assistant chatter / meta commentary, not a rule.</summary>
    AssistantProse,

    /// <summary>The trigger is a conversation fragment, not a real condition.</summary>
    MalformedTrigger,

    /// <summary>The candidate states no actionable guidance.</summary>
    MissingAction,

    /// <summary>The candidate states no reason or consequence.</summary>
    MissingReason,

    /// <summary>The candidate is too vague to be a reusable rule.</summary>
    TooVague,

    /// <summary>The candidate duplicates other noisy turn-finalizer prose.</summary>
    DuplicateNoise,

    /// <summary>The candidate is a low-value code fact, recoverable with search.</summary>
    CodeFact,

    /// <summary>The candidate is not a reusable lesson, preference, or convention.</summary>
    NotReusable,
}

/// <summary>The outcome of screening a Stop-hook capture candidate.</summary>
/// <param name="IsAcceptable">True when the candidate may proceed to the capture decision.</param>
/// <param name="Reason">Why it was rejected, when it was.</param>
public readonly record struct CandidateAssessment(bool IsAcceptable, CaptureSkipReason Reason)
{
    /// <summary>An accepted candidate.</summary>
    public static readonly CandidateAssessment Accept = new(true, CaptureSkipReason.None);

    /// <summary>A rejected candidate carrying the reason.</summary>
    public static CandidateAssessment Reject(CaptureSkipReason reason) => new(false, reason);
}

/// <summary>
/// Deterministic, offline quality gate for Stop-hook capture. It keeps assistant chatter,
/// meta commentary, malformed conversation fragments, and vague prose out of memory — so a
/// completed turn only ever produces a clean reusable lesson, preference, or convention.
///
/// The gate never calls an LLM, network, or embeddings and never blocks: it returns a
/// verdict for the caller to act on. The same filters back both the finalizer (before a
/// Pending rule is created) and <c>cleanup pending-noise</c> (to find rules already stored).
/// </summary>
public static class StopHookCandidateGate
{
    // At or below this word count, a candidate with no recognised action verb reads as a
    // fragment rather than a rule. Above it, a detailed lesson is trusted to carry guidance.
    private const int ShortCandidateWords = 8;

    // Explicit "do not persist this" instructions — English, Persian, and Finglish. Shared
    // with the turn extractor so the do-not-save vocabulary stays defined in one place.
    public static readonly string[] DoNotSaveSignals =
    [
        // English
        "do not save", "don't save", "dont save", "do not store", "don't store", "dont store",
        "do not capture", "don't capture", "dont capture", "do not remember", "don't remember",
        "dont remember", "not worth saving", "not worth storing", "don't add this to agentrecall",
        "do not add this to agentrecall", "ignore this for memory", "skip memory", "no need to save",
        "no need to store", "nothing to save", "don't save anything", "do not save anything",
        // Persian
        "ذخیره نکن", "سیو نکن", "یادت نگه ندار", "به خاطر نسپار", "کپچر نکن",
        "لازم نیست ذخیره", "ارزش ذخیره ندار", "این رو ذخیره نکن", "اینو ذخیره نکن",
        // Finglish
        "save nakon", "store nakon", "capture nakon", "zakhire nakon", "sio nakon",
        "yadet nabashe", "too agentrecall nazar", "tu agentrecall nazar", "lazem nist zakhire",
    ];

    // Assistant chatter / meta commentary that must never become a rule — English then
    // Persian/Finglish. These are statements *about* memory or the assistant's own actions,
    // not reusable guidance.
    private static readonly string[] AssistantProseSignals =
    [
        // English
        "one thing is worth saving", "one thing worth saving", "this is worth saving",
        "this might be worth capturing", "this may be worth capturing", "i would save",
        "here's what i would save", "here is what i would save", "want me to", "i'll check",
        "i will check", "i'll see what", "let me check", "i didn't explicitly", "i did not explicitly",
        "i didn't manually", "i did not manually", "i didn't call", "called no agentrecall",
        "no agentrecall tool", "the stop hook", "stop hook may have", "may have captured",
        "might have captured", "hook may have", "hook fires on its own", "i can't stop it",
        "i cannot stop it", "not properly", "here's what's actually there", "here is what's actually there",
        "same story", "leave it", "archive it", "add it properly", "worth capturing but",
        "i'll add it", "i will add it",
        // Persian / Finglish
        "به نظرم ذخیره کن", "می‌تونم ذخیره کنم", "میتونم ذخیره کنم", "میخوای ذخیره کنم",
        "می‌خوای ذخیره کنم", "من دستی ذخیره نکردم", "دستی ذخیره نکردم", "هوک ممکنه کپچر",
        "ممکنه کپچر کرده", "بذار چک کنم", "بزار چک کنم", "این خوبه ذخیره بشه",
        "mikhay zakhire", "man dasti", "bezar chek", "hook mumkene",
    ];

    // Conversation fragments that mark a trigger built from assistant prose rather than a
    // real condition. A trigger containing any of these did not come from a usable lesson.
    private static readonly string[] MalformedTriggerFragments =
    [
        "not much", "most of this chat", "this chat lives", "this chat", "here's what",
        "here is what", "one thing", "worth saving", "worth storing", "the user asked about",
        "as i mentioned", "as mentioned", "anyway", "by the way", "to summarize", "in summary",
    ];

    // Bare vague statements that carry no reusable content on their own.
    private static readonly string[] VagueSignals =
    [
        "this is important", "that's important", "it's important", "remember the workflow gotcha",
        "there is a gotcha", "there's a gotcha", "a gotcha here", "not much", "the user asked about memory",
        "keep this in mind", "good to know", "just noting", "for reference", "fyi",
    ];

    // Imperative / prescriptive verbs that mark actionable guidance. A candidate with none
    // of these (and no prohibition) has nothing to act on.
    private static readonly string[] ActionVerbs =
    [
        "use ", "prefer ", "always ", "ensure", "make sure", "should ", "must ", "need to",
        "keep ", "put ", "place ", "write ", "add ", "remove ", "split ", "merge ", "preserve",
        "validate", "verify", "check ", "pass ", "return ", "store ", "set ", "wire ", "guard",
        "handle ", "reply ", "answer ", "provide ", "explain", "match ", "configure", "wrap ",
        "avoid", "don't", "do not", "never ", "no need", "without ", "map ", "route ", "inject",
        "favor ", "default to", "prefer using",
    ];

    /// <summary>
    /// Screens a candidate's rule body. Returns the reason it should be skipped, or
    /// <see cref="CandidateAssessment.Accept"/> when it reads as a real, reusable rule.
    /// </summary>
    public static CandidateAssessment ScreenText(string? candidateText)
    {
        if (string.IsNullOrWhiteSpace(candidateText))
        {
            return CandidateAssessment.Reject(CaptureSkipReason.TooVague);
        }

        var text = candidateText.Trim();
        var lower = text.ToLowerInvariant();

        if (ContainsAny(lower, DoNotSaveSignals))
        {
            return CandidateAssessment.Reject(CaptureSkipReason.ExplicitDoNotSave);
        }

        if (ContainsAny(lower, AssistantProseSignals))
        {
            return CandidateAssessment.Reject(CaptureSkipReason.AssistantProse);
        }

        if (IsVague(text, lower))
        {
            return CandidateAssessment.Reject(CaptureSkipReason.TooVague);
        }

        // A short candidate that names no action is a fragment, not a rule. Length is the
        // guard: a long, detailed lesson is trusted to carry guidance even if its verb is
        // not in the (necessarily partial) imperative list, so real lessons never over-reject.
        var wordCount = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (!ContainsAny(lower, ActionVerbs) && wordCount <= ShortCandidateWords)
        {
            return CandidateAssessment.Reject(
                HasCondition(lower) ? CaptureSkipReason.MissingAction : CaptureSkipReason.TooVague);
        }

        return CandidateAssessment.Accept;
    }

    /// <summary>
    /// Full assessment of a candidate that is about to become a rule: the rule body must be
    /// a real rule and the trigger must be a real condition (not a conversation fragment).
    /// </summary>
    public static CandidateAssessment Assess(string? candidateText, string? triggerText)
    {
        var body = ScreenText(candidateText);
        if (!body.IsAcceptable)
        {
            return body;
        }

        return IsMalformedTrigger(triggerText)
            ? CandidateAssessment.Reject(CaptureSkipReason.MalformedTrigger)
            : CandidateAssessment.Accept;
    }

    /// <summary>True when a trigger reads as a conversation fragment rather than a condition.</summary>
    public static bool IsMalformedTrigger(string? trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return true;
        }

        var text = trigger.Trim();
        var lower = text.ToLowerInvariant();

        // Strip the synthesized "When working on …" / "When …" opener to inspect the subject.
        var subject = lower;
        foreach (var opener in new[] { "when working on ", "when ", "if ", "while ", "whenever " })
        {
            if (subject.StartsWith(opener, StringComparison.Ordinal))
            {
                subject = subject[opener.Length..];
                break;
            }
        }

        if (ContainsAny(lower, MalformedTriggerFragments) || ContainsAny(subject, MalformedTriggerFragments))
        {
            return true;
        }

        // A trigger that swallowed a full sentence (mid-string sentence break) is prose.
        if (subject.Contains(". ", StringComparison.Ordinal))
        {
            return true;
        }

        // Too short to name a real condition.
        var words = subject.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length < 2;
    }

    /// <summary>True when the text carries an explicit do-not-save instruction.</summary>
    public static bool ContainsDoNotSave(string? text) =>
        !string.IsNullOrWhiteSpace(text) && ContainsAny(text.ToLowerInvariant(), DoNotSaveSignals);

    /// <summary>A short, human-readable explanation of a skip reason for status/summary output.</summary>
    public static string Explain(CaptureSkipReason reason) => reason switch
    {
        CaptureSkipReason.ExplicitDoNotSave => "explicit do-not-save instruction",
        CaptureSkipReason.AssistantProse => "assistant prose, not a reusable rule",
        CaptureSkipReason.MalformedTrigger => "malformed trigger, not a reusable rule",
        CaptureSkipReason.MissingAction => "no actionable guidance",
        CaptureSkipReason.MissingReason => "missing reason or consequence",
        CaptureSkipReason.TooVague => "too vague to be a reusable rule",
        CaptureSkipReason.DuplicateNoise => "duplicate noisy candidate",
        CaptureSkipReason.CodeFact => "code fact, recoverable from the repository",
        CaptureSkipReason.NotReusable => "not a reusable lesson",
        _ => "not stored",
    };

    private static bool IsVague(string text, string lower)
    {
        if (ContainsAny(lower, VagueSignals))
        {
            return true;
        }

        // Very short with no action verb and no reason reads as a fragment, not a rule.
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 4 && !ContainsAny(lower, ActionVerbs))
        {
            return true;
        }

        return false;
    }

    private static bool HasCondition(string lower) =>
        lower.StartsWith("when ", StringComparison.Ordinal) ||
        lower.StartsWith("if ", StringComparison.Ordinal) ||
        lower.StartsWith("while ", StringComparison.Ordinal) ||
        lower.StartsWith("whenever ", StringComparison.Ordinal) ||
        lower.StartsWith("before ", StringComparison.Ordinal) ||
        lower.StartsWith("after ", StringComparison.Ordinal) ||
        lower.Contains(" when ", StringComparison.Ordinal);

    private static bool ContainsAny(string haystack, string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.OrdinalIgnoreCase));
}
