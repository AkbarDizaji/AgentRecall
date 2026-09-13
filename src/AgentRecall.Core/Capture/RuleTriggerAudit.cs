using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Capture;

/// <summary>What a trigger audit found wrong with one rule. A rule can carry several.</summary>
public enum TriggerFinding
{
    /// <summary>Injected often and never once accepted: it matches work it does not apply to.</summary>
    Noisy,

    /// <summary>Never injected at all: phrased in words no real task has contained.</summary>
    Dormant,

    /// <summary>Another rule carries the same action under a different trigger.</summary>
    Duplicate,

    /// <summary>The trigger cannot match reliably, per <see cref="TriggerQuality"/>.</summary>
    Unusable,
}

/// <summary>How one rule's trigger is performing, and what is wrong with it.</summary>
/// <param name="RuleId">The rule audited.</param>
/// <param name="Trigger">Its trigger, as stored.</param>
/// <param name="Injections">How many recorded retrievals injected it.</param>
/// <param name="Accepted">UserAccepted outcomes reported for it.</param>
/// <param name="Ignored">RuleIgnored outcomes reported for it.</param>
/// <param name="Findings">What the audit found, worst first; empty means healthy.</param>
/// <param name="DuplicateOf">When <see cref="TriggerFinding.Duplicate"/>, the rule it duplicates.</param>
/// <param name="Problem">When <see cref="TriggerFinding.Unusable"/>, why the trigger cannot match.</param>
public sealed record RuleTriggerReport(
    int RuleId,
    string Trigger,
    int Injections,
    int Accepted,
    int Ignored,
    IReadOnlyList<TriggerFinding> Findings,
    int? DuplicateOf = null,
    TriggerProblem Problem = TriggerProblem.None)
{
    /// <summary>Nothing to fix: the trigger matches work it applies to.</summary>
    public bool IsHealthy => Findings.Count == 0;
}

/// <summary>One rule's measured history, as the audit needs it.</summary>
/// <param name="Rule">The stored rule.</param>
/// <param name="Injections">Retrievals that injected it.</param>
/// <param name="Accepted">UserAccepted outcomes.</param>
/// <param name="Ignored">RuleIgnored outcomes.</param>
public sealed record RuleTriggerHistory(RecallRule Rule, int Injections, int Accepted, int Ignored);

/// <summary>
/// Audits triggers from the ledger rather than by reading them.
///
/// A trigger's job is to match future work the rule applies to, and AgentRecall already records
/// whether that happens: every retrieval names the rules it injected, and every reported outcome
/// says whether an injected rule helped. So the two ways a trigger fails are arithmetic, not taste.
/// A rule injected on many turns and never once accepted is matching work it has nothing to say
/// about — it is spending context budget on every one of those turns. A rule never injected at all
/// is phrased in words no real task contains, and might as well not be stored.
///
/// Pure and deterministic: given histories, it reports. Nothing here edits, archives, or re-ranks
/// anything, because which rule to fix and how is a judgement the report exists to inform.
/// </summary>
public static class RuleTriggerAudit
{
    /// <summary>
    /// Injections a rule needs before "never accepted" means anything. Below this the rule simply
    /// has not had the chance to prove itself.
    /// </summary>
    public const int NoisyInjectionFloor = 5;

    /// <summary>
    /// Reported non-applications a rule needs before it counts as noise. Silence is not evidence:
    /// a rule can be injected for weeks while nobody reports on it, and calling that noisy would
    /// condemn good rules for the ledger being thin. Noise means someone looked and it did not
    /// apply — repeatedly.
    /// </summary>
    public const int NoisyIgnoredFloor = 3;

    /// <summary>Audits every rule against its measured history, worst findings first.</summary>
    public static IReadOnlyList<RuleTriggerReport> Audit(IEnumerable<RuleTriggerHistory> histories)
    {
        ArgumentNullException.ThrowIfNull(histories);

        var all = histories.ToList();

        // Same lesson, different trigger: the earliest rule id is treated as the original, so the
        // report points at one rule to keep rather than flagging both as each other's duplicate.
        var byAction = all
            .GroupBy(h => NormalizeAction(h.Rule.RuleText), StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0 && g.Count() > 1)
            .SelectMany(g =>
            {
                var original = g.Min(h => h.Rule.Id);
                return g.Where(h => h.Rule.Id != original).Select(h => (h.Rule.Id, Original: original));
            })
            .ToDictionary(pair => pair.Id, pair => pair.Original);

        var reports = new List<RuleTriggerReport>();
        foreach (var history in all)
        {
            var findings = new List<TriggerFinding>();

            if (history.Injections >= NoisyInjectionFloor
                && history.Accepted == 0
                && history.Ignored >= NoisyIgnoredFloor)
            {
                findings.Add(TriggerFinding.Noisy);
            }

            // An always-apply rule reaches the model whether or not its words match, so its trigger
            // is not the surface that decides retrieval and "never injected" says nothing about it.
            if (history.Injections == 0 && !history.Rule.AlwaysApply)
            {
                findings.Add(TriggerFinding.Dormant);
            }

            var problem = TriggerQuality.Inspect(history.Rule.Trigger, history.Rule.RuleText);
            if (problem != TriggerProblem.None)
            {
                findings.Add(TriggerFinding.Unusable);
            }

            var duplicateOf = byAction.TryGetValue(history.Rule.Id, out var original) ? original : (int?)null;
            if (duplicateOf is not null)
            {
                findings.Add(TriggerFinding.Duplicate);
            }

            reports.Add(new RuleTriggerReport(
                history.Rule.Id,
                history.Rule.Trigger,
                history.Injections,
                history.Accepted,
                history.Ignored,
                findings,
                duplicateOf,
                problem));
        }

        // Noisiest first: a rule injected on many turns costs something on every one of them.
        return
        [
            .. reports
                .OrderByDescending(r => r.Findings.Contains(TriggerFinding.Noisy))
                .ThenByDescending(r => r.Injections)
                .ThenBy(r => r.RuleId),
        ];
    }

    /// <summary>Lowercased, punctuation-free, whitespace-collapsed action text, for duplicate matching.</summary>
    private static string NormalizeAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return string.Empty;
        }

        var letters = action.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(
            ' ',
            new string(letters).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
