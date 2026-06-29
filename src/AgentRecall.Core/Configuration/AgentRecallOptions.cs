using AgentRecall.Core.Activity;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Hooks;

namespace AgentRecall.Core.Configuration;

/// <summary>
/// Strongly-typed configuration for AgentRecall. Bound from configuration
/// sources (JSON file, environment variables) by the infrastructure layer.
/// </summary>
public sealed class AgentRecallOptions
{
    /// <summary>The configuration section name these options bind from.</summary>
    public const string SectionName = "AgentRecall";

    /// <summary>Default SQLite database file name within the data directory.</summary>
    public const string DefaultDatabaseFileName = "agentrecall.db";

    /// <summary>
    /// Directory where AgentRecall stores local data. Defaults to a folder
    /// under the user's home directory.
    /// </summary>
    public string DataDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentrecall");

    /// <summary>
    /// SQLite database file name, resolved relative to <see cref="DataDirectory"/>.
    /// </summary>
    public string DatabaseFileName { get; set; } = DefaultDatabaseFileName;

    /// <summary>Minimum log level to emit. Defaults to "Information".</summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// When true (the default), capturing feedback produces an <c>Active</c> rule
    /// straight away. Set it to false to keep captured rules <c>Pending</c> until
    /// they are explicitly approved.
    /// </summary>
    public bool AutoApproveFeedback { get; set; } = true;

    /// <summary>The absolute path to the SQLite database file.</summary>
    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);

    /// <summary>
    /// Whether the UserPromptSubmit hook injects context. When false, the hook is a
    /// no-op even if it's wired into Claude Code's settings.
    /// </summary>
    public bool HookEnabled { get; set; } = true;

    /// <summary>
    /// Keywords that mark a prompt as software-development work worth injecting
    /// context for. Single words match whole-word; multi-word entries match as phrases.
    /// </summary>
    public string[] HookKeywords { get; set; } = PromptGate.DefaultKeywords;

    /// <summary>Maximum rules the hook injects (keeps the block small).</summary>
    public int HookMaxRules { get; set; } = 5;

    /// <summary>Whether the hook may inject Pending (unapproved) rules.</summary>
    public bool HookIncludePending { get; set; }

    /// <summary>
    /// Whether the Stop-hook capture path runs. When true (the default), AgentRecall
    /// inspects the just-finished turn for a reusable lesson and stores it
    /// automatically, so capture is as deterministic as recall. When false, the
    /// capture hook is a no-op even if it's wired into Claude Code's settings.
    /// </summary>
    public bool CaptureHookEnabled { get; set; } = true;

    /// <summary>
    /// When true (the default), captured feedback is screened by the memory-worthiness
    /// classifier so low-value code facts are not stored as rules and code facts that
    /// hint at a reusable pattern are stored as the generalized lesson instead. Set it
    /// to false to store every candidate verbatim.
    /// </summary>
    public bool MemoryWorthinessEnabled { get; set; } = true;

    /// <summary>Maximum characters allowed in feedback text; longer input is rejected. Defaults to 20000.</summary>
    public int FeedbackMaxLength { get; set; } = 20_000;

    /// <summary>Maximum characters allowed in the feedback task/context; longer input is rejected. Defaults to 2000.</summary>
    public int FeedbackMaxTaskLength { get; set; } = 2_000;

    /// <summary>
    /// When true, a rejected (NotWorthStoring) candidate still records a
    /// <see cref="Domain.RecallEvent"/> for audit, even though no rule is created.
    /// Defaults to false to keep the event log clean.
    /// </summary>
    public bool StoreRejectedCandidates { get; set; }

    /// <summary>
    /// When true, an accepted candidate (e.g. an accepted PR comment) bypasses the
    /// code-fact rejection and is stored even when the classifier rates it
    /// NotWorthStoring. Defaults to false so acceptance never lowers memory quality.
    /// </summary>
    public bool AllowCodeFactsWhenAccepted { get; set; }

    /// <summary>
    /// Minimum confidence (0.0–1.0) for AgentRecall to auto-capture a worthy lesson on
    /// its own — that is, when the approve posture is on but there is no explicit
    /// acceptance signal. A worthy candidate below this bar is parked as a Pending
    /// suggestion for the user to confirm instead of being activated. Explicit
    /// acceptance always auto-captures regardless of this value. Defaults to 0.5.
    /// </summary>
    public double CaptureAutoConfidence { get; set; } = 0.5;

    /// <summary>
    /// Whether the turn finalizer runs. When true (the default), AgentRecall finalizes
    /// each completed turn — extracting reusable lessons and deciding to auto-capture,
    /// suggest, or skip — so capture is deterministic and the agent never guesses
    /// whether a lesson was kept. When false, finalization is a no-op.
    /// </summary>
    public bool TurnFinalizerEnabled { get; set; } = true;

    /// <summary>
    /// Whether the raw turn transcript is persisted with the finalization record.
    /// Defaults to false: only a content hash, the resulting rule ids, and skip reasons
    /// are stored, so transcripts never leave the machine in the database.
    /// </summary>
    public bool StoreTurnTranscript { get; set; }

    /// <summary>
    /// Maximum size (bytes) of a log file accepted by <c>import build-log</c> /
    /// <c>import test-log</c>. A larger file is rejected rather than read into memory.
    /// Defaults to 50 MB.
    /// </summary>
    public long LogImportMaxBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>
    /// Maximum characters kept per log line during import; a longer line is truncated so a
    /// single pathological line cannot blow up memory. Defaults to 8192.
    /// </summary>
    public int LogImportMaxLineLength { get; set; } = 8 * 1024;

    /// <summary>Maximum lesson candidates captured from a single turn. Defaults to 5.</summary>
    public int MaxCandidatesPerTurn { get; set; } = 5;

    /// <summary>Maximum characters kept per candidate, to bound a huge turn. Defaults to 1000.</summary>
    public int MaxCandidateCharacters { get; set; } = 1000;

    /// <summary>
    /// Whether the finalizer surfaces a user-facing notice (as a Stop-hook
    /// <c>systemMessage</c>) after a turn. Defaults to true.
    /// </summary>
    public bool FinalizerShowUserNotice { get; set; } = true;

    /// <summary>
    /// Whether a finalization that only reinforced an existing rule (a pure duplicate)
    /// stays silent rather than emitting a notice. Defaults to true.
    /// </summary>
    public bool SuppressDuplicateNotices { get; set; } = true;

    /// <summary>
    /// When true (the default), recorded outcomes adjust rule confidence on evidence
    /// and a retrieval record is written so outcomes can be attached later. When
    /// false, outcome recording is a no-op and no confidence is adjusted.
    /// </summary>
    public bool OutcomeTrackingEnabled { get; set; } = true;

    /// <summary>The confidence change applied per outcome type.</summary>
    public OutcomeConfidenceDeltas OutcomeConfidenceDeltas { get; set; } = new();

    /// <summary>
    /// How loud AgentRecall's human-facing activity notices are: <c>Silent</c>,
    /// <c>Normal</c>, or <c>Verbose</c>. Stored as a string and parsed defensively, so
    /// an unrecognised value falls back to the default rather than crashing startup.
    /// Defaults to <c>Verbose</c> so AgentRecall is visibly useful out of the box.
    /// This governs CLI/status output only — it never changes injected model context.
    /// </summary>
    public string ActivityNoticeLevel { get; set; } = nameof(NoticeLevel.Verbose);

    /// <summary>
    /// How loud the hook-injected notice is: <c>Silent</c> or <c>Normal</c>. Kept
    /// separate from <see cref="ActivityNoticeLevel"/> so the model-visible notice
    /// stays compact regardless of how verbose the human-facing notices are.
    /// <c>Verbose</c> is treated as <c>Normal</c> here. Defaults to <c>Normal</c>.
    /// </summary>
    public string HookNoticeLevel { get; set; } = nameof(NoticeLevel.Normal);

    /// <summary>
    /// Whether AgentRecall asks the user about an ambiguous capture: <c>Auto</c> (default;
    /// auto-capture strong lessons, ask only for SuggestCapture), <c>Ask</c> (also downgrade
    /// borderline auto-captures to a question), or <c>Silent</c> (never prompt). Parsed
    /// defensively so an unrecognised value falls back to <c>Auto</c>. This is distinct from
    /// <see cref="ActivityNoticeLevel"/>: this controls whether AgentRecall <em>asks</em>;
    /// the notice level controls how loud the recorded notices are.
    /// </summary>
    public string InteractiveMemoryMode { get; set; } = nameof(Capture.InteractiveMemoryMode.Auto);

    /// <summary>
    /// How much of the aggregated end-of-turn memory summary AgentRecall prints after a
    /// turn is finalized: <c>Silent</c> (none), <c>Compact</c> (one aggregate line), or
    /// <c>Detailed</c> (grouped sections with short titles). Parsed defensively so an
    /// unrecognised value falls back to <c>Compact</c>. This is distinct from
    /// <see cref="ActivityNoticeLevel"/>: that governs per-event notices, this governs the
    /// single per-turn summary. Defaults to <c>Compact</c>.
    /// </summary>
    public string TurnSummaryLevel { get; set; } = nameof(Domain.TurnSummaryLevel.Compact);

    /// <summary>The parsed interactive-memory mode, falling back to Auto when invalid.</summary>
    public InteractiveMemoryMode ResolvedInteractiveMemoryMode =>
        InteractiveMemoryModes.Resolve(InteractiveMemoryMode);

    /// <summary>The parsed turn-summary level, falling back to Compact when invalid.</summary>
    public TurnSummaryLevel ResolvedTurnSummaryLevel =>
        TurnSummaryLevels.Resolve(TurnSummaryLevel);

    /// <summary>The parsed activity notice level, falling back to Verbose when invalid.</summary>
    public NoticeLevel ResolvedActivityNoticeLevel =>
        NoticeLevels.Resolve(ActivityNoticeLevel, NoticeLevel.Verbose);

    /// <summary>The parsed hook notice level (clamped so it is never Verbose), falling back to Normal.</summary>
    public NoticeLevel ResolvedHookNoticeLevel =>
        NoticeLevels.ClampForHook(NoticeLevels.Resolve(HookNoticeLevel, NoticeLevel.Normal));
}
