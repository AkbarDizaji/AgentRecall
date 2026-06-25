using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Finalization;

/// <summary>
/// The completed turn AgentRecall finalizes. The CLI resolves the user prompt and
/// assistant response from the Stop-hook payload (inline fields or a transcript) and
/// derives the repository scope, so the finalizer itself stays pure and deterministic
/// (no file IO, no network, no LLM).
/// </summary>
public sealed record TurnFinalizationInput
{
    /// <summary>The working directory the turn ran in, if known.</summary>
    public string? Cwd { get; init; }

    /// <summary>The latest user message in the turn (a correction, request, or question).</summary>
    public string? Prompt { get; init; }

    /// <summary>The assistant's response text for the turn, if available.</summary>
    public string? AssistantResponse { get; init; }

    /// <summary>Where finalization was triggered from (e.g. <c>stop_hook</c>, <c>manual</c>).</summary>
    public string Source { get; init; } = "manual";

    /// <summary>An explicit acceptance signal from the payload, when the host provides one.</summary>
    public bool? Accepted { get; init; }

    /// <summary>Scope granularity captured rules apply to (derived from the repository).</summary>
    public ScopeLevel ScopeLevel { get; init; } = ScopeLevel.Global;

    /// <summary>Scope identifier (e.g. repository name), if any.</summary>
    public string? ScopeValue { get; init; }

    /// <summary>The raw transcript, stored only when transcript persistence is enabled.</summary>
    public string? RawTranscript { get; init; }
}

/// <summary>A lesson AgentRecall captured or suggested for the turn.</summary>
public sealed record FinalizedLesson
{
    public required int RuleId { get; init; }
    public required RuleCategory Category { get; init; }
    public required string Text { get; init; }
    public required string ScopeLabel { get; init; }
    public double Confidence { get; init; }

    /// <summary>An optional note (e.g. why it was suggested rather than captured).</summary>
    public string? Note { get; init; }
}

/// <summary>A candidate AgentRecall stored nothing for, with the reason.</summary>
public sealed record SkippedLesson
{
    public required string Reason { get; init; }

    /// <summary>The id of the existing rule this candidate duplicated, if a duplicate.</summary>
    public int? DuplicateOfRuleId { get; init; }
}

/// <summary>
/// The structured outcome of finalizing a turn: what AgentRecall captured, suggested,
/// and skipped. This is the definitive answer to "did the turn produce a lesson?", so
/// the agent never has to guess whether the Stop hook captured anything.
/// </summary>
public sealed record TurnFinalizationResult
{
    public IReadOnlyList<FinalizedLesson> Captured { get; init; } = [];
    public IReadOnlyList<FinalizedLesson> Suggested { get; init; } = [];
    public IReadOnlyList<SkippedLesson> Skipped { get; init; } = [];
    public IReadOnlyList<int> Duplicates { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>The persisted <see cref="TurnFinalization"/> id, when stored.</summary>
    public int? Id { get; init; }

    /// <summary>True when this result was returned from a prior identical finalization.</summary>
    public bool FromCache { get; init; }

    /// <summary>True when nothing of note happened (no captures, suggestions, or skips).</summary>
    public bool IsEmpty =>
        Captured.Count == 0 && Suggested.Count == 0 && Skipped.Count == 0 && Errors.Count == 0;
}
