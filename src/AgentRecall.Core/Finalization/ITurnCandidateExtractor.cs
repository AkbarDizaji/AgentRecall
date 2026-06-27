namespace AgentRecall.Core.Finalization;

/// <summary>
/// Extracts lesson candidates from a completed turn and detects the turn-level
/// acceptance and "do not save" signals. Deterministic and rule-based.
/// </summary>
public interface ITurnCandidateExtractor
{
    /// <summary>
    /// Extracts ranked lesson candidates from the user's message and the agent's
    /// response. Each candidate's text is clamped to <paramref name="maxCandidateCharacters"/>.
    /// </summary>
    IReadOnlyList<TurnLessonCandidate> Extract(string? userText, string? assistantText, int maxCandidateCharacters);

    /// <summary>True when the turn carries an explicit "do not save" instruction.</summary>
    bool HasDoNotSaveSignal(string? userText, string? assistantText);

    /// <summary>True when the user explicitly accepted or asked to keep the guidance.</summary>
    bool HasAcceptanceSignal(string? userText);

    /// <summary>
    /// Detects the outcome-aware signals in a turn (an observed failure, a user
    /// correction, an accepted review, a test that failed then passed, a repeat) so the
    /// adaptive worthiness policy can weigh what produced a candidate, not just its text.
    /// </summary>
    TurnOutcomeSignals DetectOutcomeSignals(string? userText, string? assistantText);
}
