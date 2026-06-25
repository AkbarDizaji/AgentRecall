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
}
