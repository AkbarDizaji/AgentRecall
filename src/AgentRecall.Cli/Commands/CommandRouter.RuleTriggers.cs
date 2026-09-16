using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Text;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli;

// `rules triggers` and `rules retrigger`: reviewing and repairing the surface retrieval matches on.
//
// A trigger is not prose to grade on style — it decides whether a rule ever reaches a turn it
// applies to, and whether it reaches turns it does not. Both are already measured by the retrieval
// and outcome records, so the report is arithmetic over what AgentRecall has recorded, and the
// repair keeps the rule's identity: rewriting a trigger must not cost a rule the confidence and
// outcome history it earned, which archive-and-recapture does.
public static partial class CommandRouter
{
    private static async Task<int> RuleTriggersAsync(
        string[] args,
        IServiceProvider scopedServices,
        IRecallRuleRepository rules,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args);
        var json = options.ContainsKey("json");
        var problemsOnly = !options.ContainsKey("all");

        var reports = await AuditTriggersAsync(scopedServices, rules, cancellationToken).ConfigureAwait(false);
        var shown = problemsOnly ? reports.Where(r => !r.IsHealthy).ToList() : reports;

        if (json)
        {
            WriteJson(output, new
            {
                audited = reports.Count,
                healthy = reports.Count(r => r.IsHealthy),
                rules = shown.Select(r => new
                {
                    id = r.RuleId,
                    trigger = r.Trigger,
                    injections = r.Injections,
                    accepted = r.Accepted,
                    ignored = r.Ignored,
                    findings = r.Findings.Select(f => f.ToString()),
                    duplicateOf = r.DuplicateOf,
                    problem = r.Problem == TriggerProblem.None ? null : r.Problem.ToString(),
                }),
            });
            return 0;
        }

        if (reports.Count == 0)
        {
            output.WriteLine("No rules to audit.");
            return 0;
        }

        if (shown.Count == 0)
        {
            output.WriteLine($"All {reports.Count} trigger(s) look healthy.");
            return 0;
        }

        output.WriteLine($"{shown.Count} of {reports.Count} trigger(s) need attention:");
        output.WriteLine();

        foreach (var report in shown)
        {
            output.WriteLine(
                $"#{report.RuleId}  injected {report.Injections}x, accepted {report.Accepted}, ignored {report.Ignored}");
            output.WriteLine($"    trigger: {TextTruncation.Ellipsize(report.Trigger, 100)}");
            foreach (var finding in report.Findings)
            {
                output.WriteLine($"    - {DescribeFinding(finding, report)}");
            }

            output.WriteLine();
        }

        output.WriteLine("Rewrite one with: agentrecall rules retrigger <id> --trigger \"when …\"");
        return 0;
    }

    /// <summary>Reads the ledger and audits every rule's trigger against its measured history.</summary>
    private static async Task<IReadOnlyList<RuleTriggerReport>> AuditTriggersAsync(
        IServiceProvider scopedServices,
        IRecallRuleRepository rules,
        CancellationToken cancellationToken)
    {
        var all = await rules.ListAsync(cancellationToken).ConfigureAwait(false);

        var retrievals = await scopedServices.GetRequiredService<IRetrievalRecordRepository>()
            .ListAsync(cancellationToken).ConfigureAwait(false);
        var outcomes = await scopedServices.GetRequiredService<IRuleOutcomeRepository>()
            .ListAsync(cancellationToken).ConfigureAwait(false);

        var injections = new Dictionary<int, int>();
        foreach (var record in retrievals)
        {
            foreach (var id in IdList.Parse(record.RuleIds))
            {
                injections[id] = injections.GetValueOrDefault(id) + 1;
            }
        }

        var histories = all
            .Where(rule => rule.Status != RuleStatus.Archived)
            .Select(rule => new RuleTriggerHistory(
                rule,
                injections.GetValueOrDefault(rule.Id),
                outcomes.Count(o => o.RuleId == rule.Id && o.Type == OutcomeType.UserAccepted),
                outcomes.Count(o => o.RuleId == rule.Id && o.Type == OutcomeType.RuleIgnored)));

        return RuleTriggerAudit.Audit(histories);
    }

    /// <summary>
    /// Rewrites one rule's trigger in place. The lesson is unchanged, so its confidence, outcomes
    /// and identity are kept and only the matching surface moves; the version is bumped so the
    /// change is visible in the rule's own record.
    /// </summary>
    private static async Task<int> RetriggerAsync(
        string[] args,
        IRecallRuleRepository rules,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out var ruleId))
        {
            output.WriteLine("Usage: agentrecall rules retrigger <id> --trigger \"when …\"");
            return 1;
        }

        var options = ParseOptions(args[1..]);
        if (!options.TryGetValue("trigger", out var trigger) || string.IsNullOrWhiteSpace(trigger))
        {
            output.WriteLine("Usage: agentrecall rules retrigger <id> --trigger \"when …\"");
            return 1;
        }

        var rule = await rules.GetAsync(ruleId, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            output.WriteLine($"No rule #{ruleId}.");
            return 1;
        }

        trigger = trigger.Trim();

        // The same floor capture applies, so a trigger cannot be repaired into one that still
        // cannot match. --force exists because a human editing one rule deliberately is a better
        // judge of an unusual condition than a word-count heuristic.
        var problem = TriggerQuality.Inspect(trigger, rule.RuleText);
        if (problem != TriggerProblem.None && !options.ContainsKey("force"))
        {
            output.WriteLine($"That trigger {TriggerQuality.Describe(problem)}.");
            output.WriteLine("Rephrase it, or pass --force to store it anyway.");
            return 1;
        }

        var previous = rule.Trigger;
        rule.Trigger = trigger;
        rule.Version += 1;
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        await rules.UpdateAsync(rule, cancellationToken).ConfigureAwait(false);

        output.WriteLine($"Rule #{ruleId} retriggered (v{rule.Version}), confidence {rule.Confidence:0.00} kept.");
        output.WriteLine($"  was: {TextTruncation.Ellipsize(previous, 100)}");
        output.WriteLine($"  now: {TextTruncation.Ellipsize(trigger, 100)}");
        return 0;
    }

    private static string DescribeFinding(TriggerFinding finding, RuleTriggerReport report) => finding switch
    {
        TriggerFinding.Noisy =>
            $"noisy: injected {report.Injections} times and never accepted, so it is spending context on turns it does not apply to",
        TriggerFinding.Dormant =>
            "dormant: never injected, so it is phrased in words no task has used",
        TriggerFinding.Duplicate =>
            $"duplicate: same action as #{report.DuplicateOf}, under a different trigger",
        TriggerFinding.Unusable =>
            $"unusable trigger: {TriggerQuality.Describe(report.Problem)}",
        _ => finding.ToString(),
    };
}
