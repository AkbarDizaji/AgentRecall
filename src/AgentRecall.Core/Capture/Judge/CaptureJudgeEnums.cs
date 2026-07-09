namespace AgentRecall.Core.Capture.Judge;

/// <summary>
/// What the semantic capture judge concluded should happen with a turn. The judge — not
/// AgentRecall — owns this decision; the system only validates the structured verdict and
/// persists it. See <see cref="CaptureJudgeVerdict"/>.
/// </summary>
public enum JudgeDecision
{
    /// <summary>Store the normalized rule as an active memory.</summary>
    Capture,

    /// <summary>Store the normalized rule as a pending suggestion for the user to review.</summary>
    SuggestCapture,

    /// <summary>Keep nothing; the turn holds no memory-worthy content.</summary>
    Skip,

    /// <summary>Record the lesson against an already-existing rule instead of duplicating it.</summary>
    ReinforceExisting,

    /// <summary>Replace an existing rule with the normalized rule.</summary>
    SupersedeExisting,
}

/// <summary>
/// The kind of memory the judge decided a turn contains. Mapped to a domain
/// <see cref="Domain.RuleCategory"/> by the decision mapper; several judge kinds fold onto
/// the same category (the exact judge reason is persisted separately for fidelity).
/// </summary>
public enum JudgeMemoryType
{
    /// <summary>A reusable engineering lesson.</summary>
    EngineeringLesson,

    /// <summary>A convention specific to this repository/project.</summary>
    RepositoryConvention,

    /// <summary>A durable preference about how the assistant should behave.</summary>
    UserPreference,

    /// <summary>A durable preference about how the assistant should communicate.</summary>
    CommunicationPreference,

    /// <summary>A lesson learned because the agent failed to apply a documented instruction.</summary>
    DocBackedCorrection,

    /// <summary>A convention about how a tool or workflow must be operated.</summary>
    ToolWorkflowConvention,

    /// <summary>A lesson learned from a code-review comment.</summary>
    ReviewLesson,

    /// <summary>A concrete code fact recoverable by searching the repository.</summary>
    CodeFact,

    /// <summary>Not memory-worthy content at all.</summary>
    NotMemory,
}

/// <summary>
/// Why the judge reached its decision. Richer than the domain <see cref="CaptureReason"/>
/// (it also carries the skip reasons), so it is persisted as its own value for status
/// reporting and mapped to the nearest <see cref="CaptureReason"/> only for the stored rule.
/// </summary>
public enum JudgeCaptureReason
{
    /// <summary>The user explicitly asked to save/keep/remember the guidance.</summary>
    ExplicitUserSave,

    /// <summary>The user explicitly asked not to save — an unconditional skip.</summary>
    ExplicitUserDoNotSave,

    /// <summary>The agent's own output broke or was proven wrong.</summary>
    ObservedAgentFailure,

    /// <summary>A code reviewer corrected the implementation.</summary>
    ReviewerCorrection,

    /// <summary>The user corrected the agent's behaviour.</summary>
    UserCorrection,

    /// <summary>A project/repository convention became clear.</summary>
    RepositoryConvention,

    /// <summary>The user stated a durable preference.</summary>
    UserPreference,

    /// <summary>The same mistake recurred.</summary>
    RepeatedMistake,

    /// <summary>The agent failed to apply a documented instruction and was corrected.</summary>
    DocBackedCorrection,

    /// <summary>The lesson duplicates an already-retrieved/existing rule.</summary>
    DuplicateExisting,

    /// <summary>The candidate reads as assistant chatter / meta commentary.</summary>
    AssistantProse,

    /// <summary>The candidate was read from a source document, not learned.</summary>
    SourceDocumentOnly,

    /// <summary>The candidate reads as the output of running a command.</summary>
    CommandOutputOnly,

    /// <summary>The candidate reads as a log or console line.</summary>
    LogOutputOnly,

    /// <summary>The candidate is a low-value code fact recoverable with search.</summary>
    CodeFact,

    /// <summary>The candidate is not a reusable lesson, preference, or convention.</summary>
    NotReusable,

    /// <summary>The candidate is too ambiguous to normalize into a rule.</summary>
    Ambiguous,

    /// <summary>The turn holds no memory-worthy content.</summary>
    NotMemory,
}
