using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Configuration;

/// <summary>
/// The confidence change applied per outcome type. Defaults are deterministic and
/// conservative: positive evidence nudges confidence up a little, negative evidence
/// pulls it down more sharply, and an unrecognised outcome moves nothing.
/// </summary>
public sealed class OutcomeConfidenceDeltas
{
    public double BuildPassed { get; set; } = 0.03;
    public double TestsPassed { get; set; } = 0.05;
    public double LintPassed { get; set; } = 0.02;
    public double UserAccepted { get; set; } = 0.08;
    public double UserRejected { get; set; } = -0.10;
    public double CorrectionRepeated { get; set; } = -0.15;
    public double RuleIgnored { get; set; } = -0.05;

    /// <summary>The configured delta for an outcome type (0 for Unknown).</summary>
    public double For(OutcomeType type) => type switch
    {
        OutcomeType.BuildPassed => BuildPassed,
        OutcomeType.TestsPassed => TestsPassed,
        OutcomeType.LintPassed => LintPassed,
        OutcomeType.UserAccepted => UserAccepted,
        OutcomeType.UserRejected => UserRejected,
        OutcomeType.CorrectionRepeated => CorrectionRepeated,
        OutcomeType.RuleIgnored => RuleIgnored,
        _ => 0.0,
    };
}
