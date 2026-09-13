namespace AgentRecall.Core.Outcomes;

/// <summary>
/// Turns a rule's reported outcomes into a retrieval multiplier, so a rule that keeps being
/// injected and never helping falls out of the ranking on its own.
///
/// Confidence alone does not do this. It moves slowly and by design — a rule at 0.45 confidence is
/// still eligible, still ranked, still injected on every turn whose words happen to match. The
/// evidence that it should not be is already recorded: many injections, never once accepted. This
/// folds that into the score as a dampener rather than a gate, for two reasons. A rule nobody has
/// answered for yet must not be punished for silence, so no reports means no change. And a rule
/// that was useful once keeps most of its standing even if it is ignored often afterwards, since
/// a rule that applies rarely but decisively is exactly what memory is for.
/// </summary>
public static class RuleEffectiveness
{
    /// <summary>
    /// The most a rule can be dampened. A floor rather than zero: an ignored rule is still
    /// retrievable by a task that genuinely matches it, and its outcomes may simply be stale.
    /// </summary>
    public const double Floor = 0.6;

    /// <summary>
    /// Reports needed before the dampener applies at all. Below this the ledger is too thin to
    /// distinguish a useless rule from a new one.
    /// </summary>
    public const int MinimumReports = 3;

    /// <summary>How many never-accepted reports past the threshold take a rule all the way down.</summary>
    public const int RampReports = 5;

    /// <summary>
    /// The multiplier for a rule with this outcome history. 1.0 when there is too little evidence,
    /// otherwise the share of reports in which the rule actually helped, clamped to
    /// <see cref="Floor"/>.
    /// </summary>
    public static double Factor(int accepted, int ignored)
    {
        accepted = Math.Max(0, accepted);
        ignored = Math.Max(0, ignored);

        // One acceptance is proof the rule applies to something real. A rule that fires rarely but
        // decisively is exactly what memory is for, so it is never dampened for the turns between.
        if (accepted > 0 || accepted + ignored < MinimumReports)
        {
            return 1.0;
        }

        // Never accepted: ramp down with the number of times it was injected and did not apply,
        // reaching the floor rather than falling to it on the first report past the threshold.
        var past = ignored - MinimumReports + 1;
        var ramp = Math.Min(1.0, past / (double)RampReports);
        return Math.Clamp(1.0 - ((1.0 - Floor) * ramp), Floor, 1.0);
    }
}
