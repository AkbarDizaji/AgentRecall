using AgentRecall.Core.Capture.Judge;

namespace AgentRecall.Tests;

/// <summary>
/// Shared builders for semantic-judge verdicts used across the finalizer tests. The model's
/// verdict is supplied directly so the capture path runs offline and deterministically.
/// </summary>
internal static class JudgeVerdicts
{
    public static NormalizedRule Rule(
        string action = "consume or persist the payment token before claiming the card is saved",
        string condition = "when a validator requires a payment method token",
        string because = "a validate-and-drop flow creates false guarantees for later charging",
        string title = "Consume the payment token",
        string scope = "project",
        string? avoid = "validate-and-drop flows",
        string[]? tags = null) => new()
    {
        Title = title,
        Condition = condition,
        Action = action,
        Avoid = avoid,
        Because = because,
        Scope = scope,
        Tags = tags ?? [],
    };

    public static CaptureJudgeVerdict Capture(
        double confidence = 0.9,
        JudgeCaptureReason reason = JudgeCaptureReason.ObservedAgentFailure,
        JudgeMemoryType memoryType = JudgeMemoryType.EngineeringLesson,
        NormalizedRule? rule = null) => new()
    {
        Decision = JudgeDecision.Capture,
        Confidence = confidence,
        CaptureReason = reason,
        MemoryType = memoryType,
        NormalizedRule = rule ?? Rule(),
    };

    public static CaptureJudgeVerdict Suggest(NormalizedRule? rule = null) => new()
    {
        Decision = JudgeDecision.SuggestCapture,
        Confidence = 0.65,
        CaptureReason = JudgeCaptureReason.RepositoryConvention,
        MemoryType = JudgeMemoryType.RepositoryConvention,
        NormalizedRule = rule ?? Rule(),
    };

    public static CaptureJudgeVerdict Skip(
        JudgeCaptureReason reason = JudgeCaptureReason.NotMemory, string why = "no memory-worthy content") => new()
    {
        Decision = JudgeDecision.Skip,
        Confidence = 0.1,
        CaptureReason = reason,
        MemoryType = JudgeMemoryType.NotMemory,
        WhyNotSaved = why,
    };

    public static CaptureJudgeVerdict Reinforce(int target, string notes = "same guidance already stored") => new()
    {
        Decision = JudgeDecision.ReinforceExisting,
        Confidence = 0.8,
        CaptureReason = JudgeCaptureReason.DuplicateExisting,
        MemoryType = JudgeMemoryType.EngineeringLesson,
        TargetExistingRuleId = target,
        DedupeNotes = notes,
    };
}
