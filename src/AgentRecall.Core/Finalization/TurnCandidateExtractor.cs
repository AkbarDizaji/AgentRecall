using System.Text.RegularExpressions;
using AgentRecall.Core.Abstractions;

namespace AgentRecall.Core.Finalization;

/// <summary>Where a candidate lesson was detected in the turn.</summary>
public enum TurnCandidateSource
{
    /// <summary>An imperative correction in the user's message ("use X", "don't Y").</summary>
    UserCorrection,

    /// <summary>A lesson the agent itself flagged ("one worth storing is…").</summary>
    AgentSelfIdentified,
}

/// <summary>
/// The outcome-aware signals detected in a turn: evidence that the agent actually made a
/// mistake or the user corrected behaviour, used by the adaptive worthiness policy.
/// </summary>
public sealed record TurnOutcomeSignals
{
    /// <summary>The agent's output broke or changed behaviour ("that broke behavior", "you changed semantics").</summary>
    public bool ObservedFailure { get; init; }

    /// <summary>The user corrected the agent ("no, preserve the else branch").</summary>
    public bool UserCorrection { get; init; }

    /// <summary>A review comment was applied ("the review comment was applied", "fix this based on the review").</summary>
    public bool ReviewAccepted { get; init; }

    /// <summary>A test failed and was then fixed ("tests failed because …").</summary>
    public bool TestFailedThenFixed { get; init; }

    /// <summary>How many times the same correction recurred ("this is the same mistake again" → 2).</summary>
    public int RepeatedCorrectionCount { get; init; }

    /// <summary>True when any outcome-aware evidence was detected.</summary>
    public bool HasAny =>
        ObservedFailure || UserCorrection || ReviewAccepted || TestFailedThenFixed || RepeatedCorrectionCount >= 2;
}

/// <summary>A lesson candidate extracted from a turn, with the signals used to rank it.</summary>
public sealed record TurnLessonCandidate
{
    public required string Text { get; init; }
    public required TurnCandidateSource Source { get; init; }

    /// <summary>Priority for the per-turn cap; higher is kept first.</summary>
    public required int Priority { get; init; }

    /// <summary>True when the candidate reads as a security/correctness concern.</summary>
    public bool Security { get; init; }

    /// <summary>True when the candidate is conditional ("when …", "if …").</summary>
    public bool Conditional { get; init; }

    /// <summary>True when the candidate reads as a performance concern.</summary>
    public bool Performance { get; init; }

    /// <summary>True when the candidate names a concrete code symbol (so it is repo-specific).</summary>
    public bool HasSymbol { get; init; }
}

/// <summary>
/// Deterministic, rule-based extraction of lesson candidates from a completed turn.
/// It pulls corrections from the user's message and lessons the agent flagged in its
/// own response, and detects the turn-level acceptance and "do not save" signals the
/// finalizer needs. No LLM, no embeddings — same turn in, same candidates out.
/// </summary>
public sealed class TurnCandidateExtractor : ITurnCandidateExtractor
{
    private readonly IFeedbackCandidateAnalyzer _analyzer;

    // A member access (Foo.Bar), a call (Method()), or a bare PascalCase identifier
    // marks a concrete code symbol — so the candidate is repository-specific.
    private static readonly Regex MemberOrCall =
        new(@"[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_]|\b[A-Za-z_][A-Za-z0-9_]*\(", RegexOptions.Compiled);
    private static readonly Regex PascalIdentifier =
        new(@"\b[A-Za-z_]*[a-z][A-Za-z0-9_]*[A-Z][A-Za-z0-9_]*\b", RegexOptions.Compiled);

    // A security/correctness concern stays a candidate even without an imperative verb,
    // and is ranked above ordinary guidance.
    private static readonly string[] SecuritySignals =
    [
        "authorization", "authorize", "authn", "authz", "auth ", "permission", "tenant",
        "cross-tenant", "cross tenant", "venue scope", "information disclosure", "disclose",
        "leak", "leaks", "leaking", "fail fast", "sentinel", "unreachable", "cross-layer",
        "cross layer", "validate", "validation", "security", "scope ",
    ];

    private static readonly string[] PerformanceSignals =
    [
        "re-query", "requery", "re query", "re-querying", "already loaded", "already been loaded",
        "n+1", "performance", "redundant query", "downstream", "pass it", "pass the entity",
    ];

    // Phrases by which the agent flags a lesson worth keeping.
    private static readonly string[] SelfIdentifiedSignals =
    [
        "worth storing", "worth keeping", "worth saving", "reusable lesson", "one reusable",
        "repository-scoped rule", "repository scoped rule", "real cross-layer", "this is worth",
        "the lesson here", "lesson is", "convention is", "not a code fact",
    ];

    // Lead-ins stripped from a self-identified sentence to leave just the lesson.
    private static readonly string[] SelfIdentifiedLeadIns =
    [
        "one worth storing is", "worth storing is", "this is worth storing",
        "one reusable lesson is", "a reusable lesson is", "the reusable lesson is",
        "the reusable lesson here is", "reusable lesson is", "the lesson here is",
        "the lesson is", "the convention is", "one lesson worth storing is",
    ];

    private static readonly string[] DoNotSaveSignals =
    [
        "do not save", "don't save", "dont save", "do not store", "don't store",
        "dont store", "not worth saving", "not worth storing", "skip memory",
        "no need to save", "don't remember", "do not remember", "nothing to save",
    ];

    private static readonly string[] AcceptanceSignals =
    [
        "apply the review", "apply the comment", "apply that comment", "address the comment",
        "address the review", "as the reviewer", "as suggested in the review", "accept the review",
        "do what the comment", "make the review change", "take the review comment",
        "save this", "store this", "remember this", "yes save", "please save", "do save",
        "save it", "yes, save",
    ];

    // Phrases evidencing the agent's output broke or changed behaviour.
    private static readonly string[] ObservedFailureSignals =
    [
        "that broke behavior", "that broke behaviour", "broke behavior", "broke behaviour",
        "you changed semantics", "changed semantics", "you changed the behavior",
        "you changed the behaviour", "that broke", "this broke", "you broke", "broke the build",
        "introduced a regression", "caused a regression",
    ];

    // Phrases by which the user corrects the agent.
    private static readonly string[] UserCorrectionSignals =
    [
        "no, preserve", "preserve the else", "no preserve", "that's not what i asked",
        "not what i asked", "that's wrong", "revert that", "undo that", "you should have",
        "should have preserved", "put it back",
    ];

    // Phrases by which a review comment is applied/accepted.
    private static readonly string[] ReviewAcceptedSignals =
    [
        "the review comment was applied", "review comment was applied", "applied the review comment",
        "fix this based on the review", "based on the review", "apply the review", "per the review",
        "as the reviewer", "address the review", "the reviewer was right",
    ];

    // Phrases evidencing a test that failed then was fixed.
    private static readonly string[] TestFailedSignals =
    [
        "tests failed because", "test failed because", "tests failed", "test failed",
        "the test broke", "failing test", "test was red", "tests were red",
    ];

    // Phrases marking a recurrence of the same correction.
    private static readonly string[] RepeatedCorrectionSignals =
    [
        "this is the same mistake again", "same mistake again", "this is the same mistake",
        "the same mistake", "we've been over this", "we have been over this", "as i said before",
        "i told you before", "you keep making", "keep making this mistake", "again you",
    ];

    public TurnCandidateExtractor(IFeedbackCandidateAnalyzer analyzer)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
    }

    public TurnOutcomeSignals DetectOutcomeSignals(string? userText, string? assistantText)
    {
        // Outcome evidence comes from what the user said this turn; the assistant text is
        // also scanned so a self-reported regression ("this broke the build") still counts.
        var repeated = ContainsAny(userText, RepeatedCorrectionSignals) ||
                       ContainsAny(assistantText, RepeatedCorrectionSignals);

        return new TurnOutcomeSignals
        {
            ObservedFailure = ContainsAny(userText, ObservedFailureSignals) || ContainsAny(assistantText, ObservedFailureSignals),
            UserCorrection = ContainsAny(userText, UserCorrectionSignals),
            ReviewAccepted = ContainsAny(userText, ReviewAcceptedSignals) || ContainsAny(assistantText, ReviewAcceptedSignals),
            TestFailedThenFixed = ContainsAny(userText, TestFailedSignals) || ContainsAny(assistantText, TestFailedSignals),
            // A recurrence implies at least two observations of the same correction.
            RepeatedCorrectionCount = repeated ? 2 : 0,
        };
    }

    /// <summary>True when the turn carries an explicit "do not save" instruction.</summary>
    public bool HasDoNotSaveSignal(string? userText, string? assistantText) =>
        ContainsAny(userText, DoNotSaveSignals) || ContainsAny(assistantText, DoNotSaveSignals);

    /// <summary>
    /// True when the user explicitly accepted or asked to keep the guidance. A
    /// "do not save" phrase is stripped first so "do not save this" is never misread
    /// as the "save this" acceptance signal it contains.
    /// </summary>
    public bool HasAcceptanceSignal(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            return false;
        }

        var stripped = userText;
        foreach (var marker in DoNotSaveSignals)
        {
            stripped = stripped.Replace(marker, " ", StringComparison.OrdinalIgnoreCase);
        }

        return ContainsAny(stripped, AcceptanceSignals);
    }

    public IReadOnlyList<TurnLessonCandidate> Extract(string? userText, string? assistantText, int maxCandidateCharacters)
    {
        var candidates = new List<TurnLessonCandidate>();

        // 1. Corrections in the user's message.
        foreach (var sentence in SplitSentences(userText))
        {
            if (IsDoNotSaveSentence(sentence))
            {
                continue;
            }

            var security = ContainsAny(sentence, SecuritySignals);
            var isGuidance = security || _analyzer.Analyze(sentence).IsCandidate;
            if (!isGuidance)
            {
                continue;
            }

            candidates.Add(Build(sentence, TurnCandidateSource.UserCorrection, maxCandidateCharacters));
        }

        // 2. Lessons the agent flagged in its own response.
        foreach (var sentence in SplitSentences(assistantText))
        {
            if (!ContainsAny(sentence, SelfIdentifiedSignals) || IsDoNotSaveSentence(sentence))
            {
                continue;
            }

            var lesson = StripLeadIn(sentence);
            if (!LooksSubstantive(lesson))
            {
                continue;
            }

            candidates.Add(Build(lesson, TurnCandidateSource.AgentSelfIdentified, maxCandidateCharacters));
        }

        return candidates;
    }

    private static TurnLessonCandidate Build(string text, TurnCandidateSource source, int maxCharacters)
    {
        var clamped = Clamp(text, maxCharacters);
        var lower = clamped.ToLowerInvariant();
        var security = ContainsAny(lower, SecuritySignals);
        var conditional = lower.StartsWith("when ", StringComparison.Ordinal) ||
                          lower.StartsWith("if ", StringComparison.Ordinal) ||
                          lower.Contains(" when ", StringComparison.Ordinal);
        var performance = ContainsAny(lower, PerformanceSignals);
        var hasSymbol = MemberOrCall.IsMatch(clamped) || PascalIdentifier.IsMatch(clamped);

        // Priority follows the documented order: security/correctness first, then
        // self-identified lessons, then conditional conventions, performance, generic.
        var priority =
            security ? 100
            : source == TurnCandidateSource.AgentSelfIdentified ? 70
            : conditional ? 60
            : performance ? 40
            : 20;

        return new TurnLessonCandidate
        {
            Text = clamped,
            Source = source,
            Priority = priority,
            Security = security,
            Conditional = conditional,
            Performance = performance,
            HasSymbol = hasSymbol,
        };
    }

    private static bool IsDoNotSaveSentence(string sentence) => ContainsAny(sentence, DoNotSaveSignals);

    private static string StripLeadIn(string sentence)
    {
        var trimmed = sentence.Trim();

        // Prefer the clause after a colon ("One worth storing is: when …").
        var colon = trimmed.IndexOf(':');
        if (colon >= 0 && colon < trimmed.Length - 1)
        {
            var after = trimmed[(colon + 1)..].Trim();
            if (after.Length > 0)
            {
                return after;
            }
        }

        var lower = trimmed.ToLowerInvariant();
        foreach (var lead in SelfIdentifiedLeadIns)
        {
            var idx = lower.IndexOf(lead, StringComparison.Ordinal);
            if (idx >= 0)
            {
                return trimmed[(idx + lead.Length)..].Trim().TrimStart(':', '-', ' ');
            }
        }

        return trimmed;
    }

    private static bool LooksSubstantive(string text)
    {
        // A bare "want me to save it?" leaves nothing to store; require real content.
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 4 && text.Length >= 16;
    }

    private static string Clamp(string text, int maxCharacters)
    {
        var trimmed = text.Trim();
        if (maxCharacters <= 0 || trimmed.Length <= maxCharacters)
        {
            return trimmed;
        }

        return trimmed[..maxCharacters].TrimEnd() + "…";
    }

    private static List<string> SplitSentences(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split(['.', '!', '?', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static bool ContainsAny(string? text, string[] markers) =>
        !string.IsNullOrWhiteSpace(text) &&
        markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
}
