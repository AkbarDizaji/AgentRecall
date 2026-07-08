using System.Text.RegularExpressions;

namespace AgentRecall.Core.Finalization;

/// <summary>
/// Where a capture candidate's text came from, when the host can tell us. This is the
/// structured signal the source/outcome-aware classifier consults <b>before</b> any regex:
/// if activity metadata already says the text is a skill/tool doc, command output, or a log
/// line, we trust it and never guess from shape. <see cref="Unknown"/> means "no metadata —
/// fall back to the regex classifier".
/// </summary>
public enum CandidateOrigin
{
    /// <summary>No structured metadata; classify from text shape.</summary>
    Unknown = 0,

    /// <summary>The latest user message in the turn.</summary>
    UserMessage,

    /// <summary>A code-review comment.</summary>
    ReviewComment,

    /// <summary>The assistant's own response text.</summary>
    AssistantMessage,

    /// <summary>A line the assistant read from a skill's documentation.</summary>
    SkillDoc,

    /// <summary>A line the assistant read from a tool's documentation / recipe.</summary>
    ToolDoc,

    /// <summary>Output captured from running a command.</summary>
    CommandOutput,

    /// <summary>A line from a log or console/stderr stream.</summary>
    LogOutput,
}

/// <summary>
/// The classified source/outcome kind of a capture candidate. Documentation, tool
/// instructions, command output, and log lines are read-only source material — they become
/// memory only when the outcome-aware decision matrix finds them paired with an observed
/// failure, an explicit save, or a confirmed repository convention.
/// </summary>
public enum CandidateSourceKind
{
    /// <summary>Nothing decisive matched; ordinary user guidance to judge on quality alone.</summary>
    Unknown = 0,

    /// <summary>Guidance from the user (a correction or request).</summary>
    UserFeedback,

    /// <summary>The user explicitly asked to save/keep the guidance.</summary>
    UserExplicitSave,

    /// <summary>The user explicitly asked <i>not</i> to save — an unconditional hard skip.</summary>
    UserExplicitDoNotSave,

    /// <summary>Assistant chatter / meta commentary about itself, the hook, or memory.</summary>
    AssistantMetaProse,

    /// <summary>An instruction read from a source document (spec/README/skill prose).</summary>
    SourceDocumentInstruction,

    /// <summary>An instruction that is part of a tool's or skill's operational recipe.</summary>
    ToolOrSkillInstruction,

    /// <summary>Text that reads as the output of running a command.</summary>
    CommandOutput,

    /// <summary>Text that reads as a log or console line.</summary>
    LogOutput,

    /// <summary>Feedback from a code review.</summary>
    ReviewFeedback,

    /// <summary>Evidence the agent's own output broke or was corrected.</summary>
    ObservedAgentFailure,

    /// <summary>A confirmed repository convention ("use the loaded entity", "we always …").</summary>
    RepositoryConventionConfirmation,
}

/// <summary>The classifier's verdict for a candidate: its kind and a named reason.</summary>
/// <param name="Kind">The classified source/outcome kind.</param>
/// <param name="Reason">A short, named explanation of why the classifier decided that.</param>
public readonly record struct CandidateClassification(CandidateSourceKind Kind, string Reason);

/// <summary>
/// Deterministic, offline, English-only source/outcome classifier for Stop-hook capture.
///
/// It answers "what <i>kind</i> of text is this candidate?" so the decision matrix can keep
/// read-only source material — documentation, tool instructions, command output, and logs —
/// out of memory unless it is paired with an observed failure, an explicit save, or a
/// confirmed repository convention.
///
/// Order of evidence (see <see cref="Classify"/>):
/// <list type="number">
///   <item>structured <see cref="CandidateOrigin"/> metadata, when the host provides it;</item>
///   <item>a small set of compiled, timeout-guarded regex pattern groups otherwise.</item>
/// </list>
///
/// The patterns are shape-based (a CLI flag, an ALL_CAPS placeholder, a log level, a command
/// invocation), not a blacklist of exact sentences, and every one carries a match timeout so
/// the classifier can never stall the non-blocking Stop hook.
/// </summary>
public static class CandidateSourceClassifier
{
    private const RegexOptions BaseOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Every pattern is bounded and simple, but we still cap match time: this runs inside the
    // non-blocking Stop hook, so a pathological input must never hold it up.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>An explicit "do not persist this" instruction (English).</summary>
    public static readonly Regex ExplicitDoNotSaveIntentPattern = new(
        @"\bdo(?:n['’]?t| not)\s+(?:save|store|capture|remember)\b" +
        @"|\bnot\s+worth\s+(?:saving|storing)\b" +
        @"|\bno\s+need\s+to\s+(?:save|store)\b" +
        @"|\b(?:skip\s+memory|nothing\s+to\s+save|ignore\s+this\s+for\s+memory)\b" +
        @"|\bdo(?:n['’]?t| not)\s+add\s+this\s+to\s+agentrecall\b",
        BaseOptions | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>An explicit "keep/save this" instruction (English).</summary>
    public static readonly Regex ExplicitSaveIntentPattern = new(
        @"\b(?:save|store|remember)\s+(?:this|it|that)\b" +
        @"|\bplease\s+save\b|\byes,?\s+save\b|\bdo\s+save\b",
        BaseOptions | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>Evidence the agent's output broke or the user corrected it ("instead of …", "broke the build").</summary>
    public static readonly Regex ObservedFailureOrCorrectionPattern = new(
        @"\binstead\s+of\b" +
        @"|\b(?:that|this|you)\s+broke\b|\bbroke\s+the\s+build\b" +
        @"|\b(?:introduced|caused)\s+a\s+regression\b" +
        @"|\bchanged\s+(?:the\s+)?(?:semantics|behaviou?r)\b" +
        @"|\bno,?\s+preserve\b|\bthat['’]?s\s+wrong\b|\brevert\s+that\b" +
        @"|\bshould\s+have\b|\bsame\s+mistake\s+again\b",
        BaseOptions | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>A confirmed repository convention ("use the loaded entity", "re-query", "in this repo we …").</summary>
    public static readonly Regex RepositoryConfirmationPattern = new(
        @"\bre-?quer(?:y|ying|ied)\b" +
        @"|\bthe\s+(?:existing|loaded|already-loaded)\s+\w+\b" +
        @"|\b(?:this\s+is|that['’]?s)\s+(?:the|our)\s+convention\b" +
        @"|\brepository\s+convention\b" +
        @"|\bin\s+this\s+(?:repo|repository|codebase|project)\b" +
        @"|\bwe\s+(?:always|already)\s+\w+\b",
        BaseOptions | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>Assistant chatter / meta commentary about itself, the Stop hook, or memory.</summary>
    public static readonly Regex AssistantMetaProsePattern = new(
        @"\bone\s+thing\s+(?:is\s+)?worth\s+saving\b|\bworth\s+(?:saving|capturing)\b" +
        @"|\bwant\s+me\s+to\b|\bi['’]?ll\s+(?:check|see|add\s+it)\b|\blet\s+me\s+check\b" +
        @"|\bi\s+did\s?n['’]?t\s+(?:manually\s+|explicitly\s+)?(?:call|save|store)\b" +
        @"|\bthe\s+stop\s+hook\b|\bhook\s+(?:may|might)\s+have\b|\bmay\s+have\s+captured\b" +
        @"|\bhere['’]?s\s+what\s+i(?:['’]?d|\s+would)\s+save\b",
        BaseOptions | RegexOptions.IgnoreCase, MatchTimeout);

    /// <summary>Text that reads as a command invocation or command output.</summary>
    public static readonly Regex CommandOutputPattern = new(
        @"(?<![\w-])(?:git|npm|npx|pnpm|yarn|dotnet|docker|kubectl|make|cargo|pip|python|node|curl|grep|sed|awk)\s+[a-z][\w.:-]*" +
        @"|^\s*\$\s",
        BaseOptions | RegexOptions.IgnoreCase | RegexOptions.Multiline, MatchTimeout);

    // Log levels are matched case-sensitively (uppercase) so ordinary prose ("handle the
    // error", "for your info") is not mistaken for a log line. The operational-conflict
    // alternatives use inline (?i:) because they read naturally in mixed case.
    /// <summary>Text that reads as a log or console line (a level token, a timestamp, an operational conflict note).</summary>
    public static readonly Regex LogOutputPattern = new(
        @"\b(?:ERROR|WARN|WARNING|INFO|DEBUG|TRACE|FATAL|EXCEPTION)\b" +
        @"|\b\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}\b" +
        @"|(?i:\b(?:leftover|previous\s+run|port\s+conflict)\b)" +
        @"|(?i:\bwill\s+cause\b[^.]{0,40}\bconflict)",
        BaseOptions, MatchTimeout);

    // The ALL_CAPS placeholder alternative is matched case-sensitively so it fires on
    // RESULTS_DIR but not on lowercase words; it also requires an underscore-joined segment so
    // plain acronyms in real rules (JSON, SQL, HTTP) are never mistaken for placeholders.
    /// <summary>An instruction that is part of a tool/skill recipe (a CLI flag, an ALL_CAPS placeholder, "for subsequent steps").</summary>
    public static readonly Regex ToolOrSkillInstructionPattern = new(
        @"(?i:(?<![\w-])--[a-z][\w-]+)" +
        @"|\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\b" +
        @"|(?i:\bfor\s+(?:the\s+)?(?:subsequent|next|following)\s+steps?\b)",
        BaseOptions, MatchTimeout);

    /// <summary>An instruction read from a source document (a cross-reference to a section/guide/spec).</summary>
    public static readonly Regex SourceDocumentInstructionPattern = new(
        @"(?i:\b(?:see|refer\s+to)\s+(?:the\s+)?(?:section|chapter|guide|doc(?:ument(?:ation)?)?|readme|appendix)\b)" +
        @"|(?i:\bas\s+(?:described|documented|noted|shown)\s+(?:in|above|below)\b)" +
        @"|(?i:\bper\s+the\b[^.]{0,30}\b(?:doc(?:umentation)?|guide|spec|readme)\b)",
        BaseOptions, MatchTimeout);

    /// <summary>
    /// Classifies a candidate. Structured <paramref name="origin"/> metadata is trusted first;
    /// with none, the regex pattern groups decide, most-decisive intent first.
    /// </summary>
    public static CandidateClassification Classify(string? text, CandidateOrigin origin = CandidateOrigin.Unknown)
    {
        // 1. Structured metadata wins: if the host already told us this text is doc / tool /
        //    command / log material, we never second-guess it with regex.
        switch (origin)
        {
            case CandidateOrigin.SkillDoc:
                return new(CandidateSourceKind.SourceDocumentInstruction, "structured metadata: skill-doc");
            case CandidateOrigin.ToolDoc:
                return new(CandidateSourceKind.ToolOrSkillInstruction, "structured metadata: tool-doc");
            case CandidateOrigin.CommandOutput:
                return new(CandidateSourceKind.CommandOutput, "structured metadata: command-output");
            case CandidateOrigin.LogOutput:
                return new(CandidateSourceKind.LogOutput, "structured metadata: log-output");
        }

        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return new(CandidateSourceKind.Unknown, "empty candidate");
        }

        // 2. Regex fallback. Explicit intent first, then outcome evidence (so "use X instead of
        //    Y" reads as a correction, not doc text), then read-only source shapes.
        if (Matches(ExplicitDoNotSaveIntentPattern, value))
        {
            return new(CandidateSourceKind.UserExplicitDoNotSave, "explicit do-not-save intent");
        }

        if (Matches(ExplicitSaveIntentPattern, value))
        {
            return new(CandidateSourceKind.UserExplicitSave, "explicit save intent");
        }

        if (Matches(ObservedFailureOrCorrectionPattern, value))
        {
            return new(CorrectionKind(origin), "observed failure or correction");
        }

        if (Matches(RepositoryConfirmationPattern, value))
        {
            return new(CandidateSourceKind.RepositoryConventionConfirmation, "repository-convention confirmation");
        }

        if (Matches(AssistantMetaProsePattern, value))
        {
            return new(CandidateSourceKind.AssistantMetaProse, "assistant meta-prose shape");
        }

        if (Matches(CommandOutputPattern, value))
        {
            return new(CandidateSourceKind.CommandOutput, "command-output shape");
        }

        if (Matches(LogOutputPattern, value))
        {
            return new(CandidateSourceKind.LogOutput, "log-output shape");
        }

        if (Matches(ToolOrSkillInstructionPattern, value))
        {
            return new(CandidateSourceKind.ToolOrSkillInstruction, "tool/skill instruction shape");
        }

        if (Matches(SourceDocumentInstructionPattern, value))
        {
            return new(CandidateSourceKind.SourceDocumentInstruction, "source-document instruction shape");
        }

        // 3. Nothing decisive: ordinary guidance, left for the quality gate to judge.
        return new(FeedbackKind(origin), "no source/outcome pattern matched");
    }

    /// <summary>True when the text confirms a repository convention — the pairing signal that
    /// lets otherwise read-only source text through the decision matrix.</summary>
    public static bool MatchesRepositoryConfirmation(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Matches(RepositoryConfirmationPattern, text.Trim());

    // A correction/failure phrase is attributed by where it came from: a review comment is
    // review feedback, a user message is user feedback, and anything else (the agent's own
    // text, or no metadata) reads as an observed agent failure.
    private static CandidateSourceKind CorrectionKind(CandidateOrigin origin) => origin switch
    {
        CandidateOrigin.ReviewComment => CandidateSourceKind.ReviewFeedback,
        CandidateOrigin.UserMessage => CandidateSourceKind.UserFeedback,
        _ => CandidateSourceKind.ObservedAgentFailure,
    };

    private static CandidateSourceKind FeedbackKind(CandidateOrigin origin) => origin switch
    {
        CandidateOrigin.ReviewComment => CandidateSourceKind.ReviewFeedback,
        _ => CandidateSourceKind.UserFeedback,
    };

    // Fail open (no match) on a timeout: the classifier must never throw into the Stop hook,
    // and the existing quality gate still screens whatever we let through.
    private static bool Matches(Regex pattern, string value)
    {
        try
        {
            return pattern.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
