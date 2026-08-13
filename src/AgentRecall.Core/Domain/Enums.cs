namespace AgentRecall.Core.Domain;

/// <summary>Lifecycle state of a <see cref="RecallRule"/>.</summary>
public enum RuleStatus
{
    Draft = 0,
    Active = 1,
    Superseded = 2,
    Retired = 3,

    /// <summary>Extracted from feedback but not yet reviewed/promoted.</summary>
    Pending = 4,

    /// <summary>Reviewed and accepted as a high-quality, applicable rule.</summary>
    Promoted = 5,

    /// <summary>Retired/rejected and kept only for history; excluded from search.</summary>
    Archived = 6,
}

/// <summary>
/// Where a <see cref="RecallRule"/> came from. Distinct from <see cref="RuleCategory"/>
/// (what kind of knowledge it is): a seed rule can still be a repository-convention or
/// engineering-lesson category rule. Defaults to <see cref="Learned"/> so every rule
/// captured before seed packs existed keeps its original meaning.
/// </summary>
public enum RuleSource
{
    /// <summary>
    /// Learned locally from this project — feedback, corrections, mined lessons, imports.
    /// The default and the only source before seed packs existed.
    /// </summary>
    Learned = 0,

    /// <summary>
    /// Installed from a curated built-in seed pack (see <c>agentrecall seed</c>). Starter
    /// guidance, not a project fact: ranked below learned rules and clearly marked as
    /// seed-derived until repeated successful local use earns it more trust.
    /// </summary>
    BuiltInSeed = 1,
}

/// <summary>
/// The granularity at which a rule or scope applies, from broadest to narrowest.
/// </summary>
public enum ScopeLevel
{
    Global = 0,
    Language = 1,
    Repository = 2,
    Directory = 3,
    File = 4,
}

/// <summary>
/// What kind of knowledge a <see cref="RecallRule"/> captures. Drives whether a
/// candidate is stored and how much it is trusted.
/// </summary>
public enum RuleCategory
{
    /// <summary>Not yet classified (the default for rules from earlier versions).</summary>
    Unknown = 0,

    /// <summary>
    /// Describes what exists in code (a member, a file path, one component calling
    /// another). Recoverable with search, so rejected by default.
    /// </summary>
    CodeFact = 1,

    /// <summary>
    /// Tells the agent what to use in this repository under a specific condition.
    /// Stored, usually repo-scoped, with lower default trust than a lesson.
    /// </summary>
    RepositoryConvention = 2,

    /// <summary>
    /// A reusable why/pattern that survives refactors (consistency rules, bug
    /// patterns, reasoned principles). Stored with higher default trust.
    /// </summary>
    EngineeringLesson = 3,

    /// <summary>
    /// A durable preference the user stated about how the assistant should behave,
    /// that is not specifically about communication (e.g. a workflow or interaction
    /// preference). Applies to the user, not to a repository, so it is never a
    /// repository convention. Captured with high trust when stated explicitly.
    /// </summary>
    UserPreference = 4,

    /// <summary>
    /// A durable preference about how the assistant should communicate: answer
    /// length, explanation depth, formatting, language, prompt delivery, how often
    /// to ask questions. A specialization of <see cref="UserPreference"/> for the
    /// communication surface; scoped to the user, never to a repository.
    /// </summary>
    CommunicationPreference = 5,
}

/// <summary>The kind of activity recorded by a <see cref="RecallEvent"/>.</summary>
public enum RecallEventType
{
    RuleCreated = 0,
    RuleUpdated = 1,
    RuleApplied = 2,
    RuleSuperseded = 3,
    MistakeObserved = 4,

    /// <summary>Several rules were merged into a single canonical rule.</summary>
    RulesCompressed = 5,

    /// <summary>A rule was promoted to high-trust status.</summary>
    RulePromoted = 6,

    /// <summary>A rule was archived (retired from search).</summary>
    RuleArchived = 7,

    /// <summary>A captured candidate was rejected as not memory-worthy.</summary>
    RuleRejected = 8,

    /// <summary>A rule was permanently removed from the database.</summary>
    RuleDeleted = 9,

    /// <summary>A client reported a rule as wrong, corrupted, or unusable; it was archived on the spot.</summary>
    RuleReportedBad = 10,
}

/// <summary>
/// The real-world result observed after a rule was retrieved, used to move its
/// confidence on evidence rather than leave it static.
/// </summary>
public enum OutcomeType
{
    /// <summary>No recognised outcome; records no confidence change.</summary>
    Unknown = 0,

    /// <summary>A build passed after the rule was injected.</summary>
    BuildPassed = 1,

    /// <summary>Tests passed after the rule was injected.</summary>
    TestsPassed = 2,

    /// <summary>Linting passed after the rule was injected.</summary>
    LintPassed = 3,

    /// <summary>The user accepted the work the rule guided.</summary>
    UserAccepted = 4,

    /// <summary>The user rejected the work the rule guided.</summary>
    UserRejected = 5,

    /// <summary>The same correction recurred even though the rule was injected.</summary>
    CorrectionRepeated = 6,

    /// <summary>The rule was retrieved but went unused.</summary>
    RuleIgnored = 7,
}

/// <summary>Lifecycle of a mined <see cref="RecallRule"/> candidate awaiting review.</summary>
public enum LessonCandidateStatus
{
    /// <summary>Proposed by mining; awaiting human review.</summary>
    Suggested = 0,

    /// <summary>Accepted into a real rule.</summary>
    Accepted = 1,

    /// <summary>Rejected; its pattern is suppressed from future proposals.</summary>
    Rejected = 2,
}

/// <summary>A suggested lifecycle action for a <see cref="RecallRule"/>.</summary>
public enum RecommendationType
{
    /// <summary>Promote a strong, well-evidenced rule.</summary>
    Promote = 0,

    /// <summary>Archive a stale, low-value, or superseded rule.</summary>
    Archive = 1,

    /// <summary>Replace a weaker/older rule with a stronger one.</summary>
    Supersede = 2,

    /// <summary>Flag a risky or low-quality rule for human review.</summary>
    Review = 3,

    /// <summary>Lower a rule's confidence on negative evidence.</summary>
    LowerConfidence = 4,

    /// <summary>Raise a rule's confidence on positive evidence.</summary>
    RaiseConfidence = 5,
}

/// <summary>
/// How loud AgentRecall's user-facing activity notices are. Drives how much detail
/// a notice carries; it never changes what AgentRecall actually does.
/// </summary>
public enum NoticeLevel
{
    /// <summary>No user-visible notice is emitted (errors still go to stderr/logs).</summary>
    Silent = 0,

    /// <summary>A concise one-line summary only.</summary>
    Normal = 1,

    /// <summary>The summary plus useful per-item detail bullets.</summary>
    Verbose = 2,
}

/// <summary>
/// How much of the aggregated end-of-turn memory summary AgentRecall prints after a
/// turn is finalized. This is distinct from <see cref="NoticeLevel"/>: the notice level
/// controls per-event notices, while this controls the single per-turn summary.
/// </summary>
public enum TurnSummaryLevel
{
    /// <summary>No automatic end-of-turn summary is printed (status commands still work).</summary>
    Silent = 0,

    /// <summary>One short aggregate line: used / captured / suggested / skipped counts.</summary>
    Compact = 1,

    /// <summary>Grouped sections with short rule titles and reasons (bounded; no full bodies).</summary>
    Detailed = 2,
}

/// <summary>
/// The kind of user-facing activity AgentRecall recorded. Stored on an
/// <see cref="AgentRecallActivity"/> so the activity log can be filtered and rendered.
/// </summary>
public enum ActivityType
{
    /// <summary>Relevant rules were retrieved for a task (inject-context, hook).</summary>
    ContextFetched = 0,

    /// <summary>A new rule was captured.</summary>
    RuleCaptured = 1,

    /// <summary>A rule was suggested (Pending) for review.</summary>
    RuleSuggested = 2,

    /// <summary>A capture candidate was skipped (duplicate or not memory-worthy).</summary>
    CandidateSkipped = 3,

    /// <summary>A rule conflict was resolved and changed what was injected.</summary>
    ConflictResolved = 4,

    /// <summary>Lesson candidates were mined from repeated signals.</summary>
    LessonMined = 5,

    /// <summary>Lifecycle actions (promote/archive/supersede/review) were recommended.</summary>
    LifecycleRecommended = 6,

    /// <summary>A completed turn was finalized (captured/suggested/skipped).</summary>
    TurnFinalized = 7,

    /// <summary>The user explicitly checked capture/finalization status.</summary>
    StatusChecked = 8,

    /// <summary>A suggested (Pending) rule was remembered (approved) via Interactive Memory.</summary>
    SuggestionRemembered = 9,

    /// <summary>A suggested (Pending) rule was ignored (archived) via Interactive Memory.</summary>
    SuggestionIgnored = 10,

    /// <summary>A built-in seed pack was installed.</summary>
    SeedInstalled = 11,

    /// <summary>Seed rules gained confidence from repeated uneventful use.</summary>
    SeedReinforced = 12,

    /// <summary>The end-of-turn career-impact detector flagged possible Staff-level impact.</summary>
    CareerImpactDetected = 13,

    /// <summary>The host-supplied document-opportunity judge offered a document to generate.</summary>
    DocOpportunityDetected = 14,

    /// <summary>
    /// The Stop hook declined to finish a turn without a semantic capture judgment and asked the
    /// session model to submit one. Recorded so "why did the turn resume?" is answerable from
    /// state rather than guessed.
    /// </summary>
    JudgmentRequested = 15,
}

/// <summary>
/// How the optional career-impact detector behaves at the end of a turn. Only takes effect
/// when the <c>career-impact</c> seed pack is installed; otherwise the detector never runs.
/// </summary>
public enum CareerImpactMode
{
    /// <summary>Never run the automatic summary. The <c>career</c> commands still work.</summary>
    Silent = 0,

    /// <summary>Run the cheap detector; print a compact summary only for significant work.</summary>
    SignificantOnly = 1,

    /// <summary>Run the detector and print a bounded summary even for lower-confidence candidates.</summary>
    Always = 2,
}

/// <summary>How much of the automatic career-impact summary is printed.</summary>
public enum CareerImpactSummaryLevel
{
    /// <summary>At most five bullets plus a pointer to the on-demand journal command.</summary>
    Compact = 0,

    /// <summary>Bounded impact/evidence/metrics/stakeholders/ADR/promotion sections.</summary>
    Detailed = 1,
}

/// <summary>Review state of a persisted <see cref="CareerImpactCandidate"/>.</summary>
public enum CareerImpactStatus
{
    /// <summary>Detected and awaiting the user (the default).</summary>
    Open = 0,

    /// <summary>The user dismissed it as not promotion-worthy.</summary>
    Dismissed = 1,

    /// <summary>A career journal entry was generated from it.</summary>
    Journaled = 2,
}

/// <summary>
/// A dimension of engineering impact a <see cref="CareerImpactCandidate"/> touches. Used to
/// label detected work; ordering is not significant, so new values may be appended freely.
/// </summary>
public enum ImpactCategory
{
    TechnicalImpact = 0,
    BusinessImpact = 1,
    UserImpact = 2,
    CrossTeamImpact = 3,
    LongTermLeverage = 4,
    Reliability = 5,
    Security = 6,
    Performance = 7,
    DeveloperProductivity = 8,
    Cost = 9,
    Leadership = 10,
    Documentation = 11,
    Architecture = 12,
    IncidentResponse = 13,
    ProcessImprovement = 14,
    PromotionEvidence = 15,
}

/// <summary>The kind of durable document the document-opportunity judge can offer to generate.
/// Stored via a string conversion wherever it is persisted, so ordering is not significant.</summary>
public enum DocumentType
{
    Incident = 0,
    Rfc = 1,
    Proposal = 2,
    Adr = 3,
    Postmortem = 4,
    Runbook = 5,
}

/// <summary>
/// Whether the host-supplied document-opportunity judge runs at the end of a turn. Unlike
/// <see cref="CareerImpactMode"/> there is no deterministic detector to grade here — the judge
/// already returns a final offer-or-skip decision, so this is a bare on/off toggle, mirroring
/// <see cref="Capture.CaptureJudgeMode"/>.
/// </summary>
public enum DocOpportunityMode
{
    /// <summary>Never run the judge. The <c>document</c> commands still work.</summary>
    Off = 0,

    /// <summary>Run the host-supplied judge and surface an offered document as a turn-summary pointer.</summary>
    Semantic = 1,
}

/// <summary>Review state of a persisted <see cref="DocOpportunityCandidate"/>.</summary>
public enum DocOpportunityStatus
{
    /// <summary>Offered and awaiting the user (the default).</summary>
    Open = 0,

    /// <summary>The user declined to generate the document.</summary>
    Dismissed = 1,

    /// <summary>The document was generated and written to disk.</summary>
    Written = 2,
}

/// <summary>Review state of a <see cref="RuleLifecycleRecommendation"/>.</summary>
public enum RecommendationStatus
{
    /// <summary>Proposed; awaiting a human decision.</summary>
    Suggested = 0,

    /// <summary>Accepted but not (yet) applied as a mutation (e.g. Review).</summary>
    Accepted = 1,

    /// <summary>Dismissed; suppressed from being proposed again.</summary>
    Rejected = 2,

    /// <summary>The recommended action was carried out.</summary>
    Applied = 3,
}
