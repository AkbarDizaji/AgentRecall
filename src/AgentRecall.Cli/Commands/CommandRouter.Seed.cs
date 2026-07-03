using AgentRecall.Core.Activity;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Seeds;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentRecall.Cli;

// The `seed` command group: opt-in curated starter rules (seed packs).
public static partial class CommandRouter
{
    /// <summary>
    /// Manages built-in seed packs: list/show the catalog, install a pack (idempotently),
    /// remove it, and report installed status. Seed packs are never installed automatically.
    /// </summary>
    private static async Task<int> SeedAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sub = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : string.Empty;
        var options = ParseOptions(args);
        var json = options.ContainsKey("json");

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var seeds = scope.ServiceProvider.GetRequiredService<ISeedPackService>();

        switch (sub)
        {
            case "list":
                return await SeedListAsync(seeds, output, json, cancellationToken).ConfigureAwait(false);

            case "show":
                return await SeedShowAsync(seeds, PackArg(args), output, json, cancellationToken).ConfigureAwait(false);

            case "install":
                return await SeedInstallAsync(scope, seeds, PackArg(args), options, services, output, logger, cancellationToken).ConfigureAwait(false);

            case "remove":
                return await SeedRemoveAsync(seeds, PackArg(args), options, output, json, cancellationToken).ConfigureAwait(false);

            case "status":
                return await SeedStatusAsync(seeds, output, json, cancellationToken).ConfigureAwait(false);

            default:
                SeedUsage(output);
                return 1;
        }
    }

    // The pack name is the first positional token after the subcommand.
    private static string? PackArg(string[] args) =>
        args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;

    private static async Task<int> SeedListAsync(ISeedPackService seeds, TextWriter output, bool json, CancellationToken cancellationToken)
    {
        var packs = await seeds.ListAsync(cancellationToken).ConfigureAwait(false);

        if (json)
        {
            WriteJson(output, packs.Select(p => new
            {
                name = p.Name,
                description = p.Description,
                ruleCount = p.RuleCount,
                installed = p.Installed,
            }).ToList());
            return 0;
        }

        output.WriteLine("Available seed packs:");
        foreach (var pack in packs)
        {
            output.WriteLine();
            output.WriteLine($"- {pack.Name}");
            output.WriteLine($"  {pack.RuleCount} rules");
            output.WriteLine($"  {pack.Description}");
            output.WriteLine($"  Status: {(pack.Installed ? "installed" : "not installed")}");
        }

        return 0;
    }

    private static async Task<int> SeedShowAsync(ISeedPackService seeds, string? pack, TextWriter output, bool json, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pack))
        {
            output.WriteLine("Usage: agentrecall seed show <pack>");
            return 1;
        }

        var detail = await seeds.ShowAsync(pack, cancellationToken).ConfigureAwait(false);
        if (detail is null)
        {
            output.WriteLine($"Unknown seed pack '{pack}'. Run 'agentrecall seed list' to see available packs.");
            return 1;
        }

        if (json)
        {
            WriteJson(output, new
            {
                name = detail.Name,
                description = detail.Description,
                copyrightNote = detail.CopyrightNote,
                ruleCount = detail.RuleCount,
                defaultStatus = detail.DefaultStatus.ToString(),
                defaultConfidence = detail.DefaultConfidence,
                installed = detail.Installed,
                rules = detail.Rules.Select(r => new
                {
                    key = r.Key,
                    title = r.Title,
                    ruleId = r.RuleId,
                    status = r.Status?.ToString(),
                }).ToList(),
            });
            return 0;
        }

        output.WriteLine($"Seed pack: {detail.Name}");
        output.WriteLine(detail.Description);
        output.WriteLine();
        output.WriteLine($"Rules:            {detail.RuleCount}");
        output.WriteLine($"Default status:   {StatusLabel(detail.DefaultStatus)}");
        output.WriteLine($"Default confidence: {detail.DefaultConfidence:0.00}");
        output.WriteLine($"Installed:        {(detail.Installed ? "yes" : "no")}");
        output.WriteLine();
        output.WriteLine($"Note: {detail.CopyrightNote}");
        output.WriteLine();
        output.WriteLine("Rules in this pack:");
        foreach (var rule in detail.Rules)
        {
            var installed = rule.RuleId is { } id ? $" (#{id}, {rule.Status})" : string.Empty;
            output.WriteLine($"- {rule.Title}{installed}");
        }

        return 0;
    }

    private static async Task<int> SeedInstallAsync(
        AsyncServiceScope scope,
        ISeedPackService seeds,
        string? pack,
        Dictionary<string, string> options,
        IServiceProvider services,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pack))
        {
            output.WriteLine("Usage: agentrecall seed install <pack> [--active] [--suggested] [--force] [--json]");
            return 1;
        }

        // Default is Active; --active is the explicit form of the default; --suggested is the
        // conservative opt-in that installs rules as Pending for manual approval.
        var installOptions = new SeedInstallOptions
        {
            Suggested = options.ContainsKey("suggested"),
            Force = options.ContainsKey("force"),
            Source = "cli",
        };

        SeedInstallResult result;
        try
        {
            result = await seeds.InstallAsync(pack, installOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            output.WriteLine(ex.Message);
            return 1;
        }

        var notice = ActivityNoticeFactory.ForSeedInstalled(result, "cli");
        if (notice is not null)
        {
            await scope.ServiceProvider.GetRequiredService<IActivityRecorder>()
                .RecordAsync(notice, cancellationToken).ConfigureAwait(false);
        }

        if (options.ContainsKey("json"))
        {
            WriteJson(output, SeedInstallJson(result));
            return 0;
        }

        var statusWord = result.Status == RuleStatus.Active ? "Active" : "Suggested";
        output.WriteLine($"🧠 **AgentRecall:** installed seed pack `{result.Pack}`.");
        if (result.Added > 0)
        {
            output.WriteLine($"- {result.Added} seed rules installed as {statusWord} with moderate confidence.");
        }

        if (result.Restored > 0)
        {
            output.WriteLine($"- {result.Restored} previously removed rule(s) restored as {statusWord}.");
        }

        if (result.Skipped > 0)
        {
            var archived = result.Changes.Count(c => c.Outcome == SeedRuleOutcome.SkippedArchived);
            var modified = result.Changes.Count(c => c.Outcome == SeedRuleOutcome.SkippedUserModified);
            var existing = result.Changes.Count(c => c.Outcome == SeedRuleOutcome.SkippedExisting);
            var detail = new List<string>();
            if (existing > 0) detail.Add($"{existing} already installed");
            if (archived > 0) detail.Add($"{archived} previously removed — use --force to restore");
            if (modified > 0) detail.Add($"{modified} kept your edits");
            output.WriteLine($"- {result.Skipped} skipped ({string.Join("; ", detail)})");
        }

        output.WriteLine($"- Initial confidence: {result.Confidence:0.00}");
        output.WriteLine("- Seed rules are starter guidance. Project-specific rules and explicit user corrections override them.");
        if (result.Status == RuleStatus.Pending)
        {
            output.WriteLine("- Use `agentrecall rules approve <id>` to activate individual rules");
        }

        output.WriteLine($"- Use `agentrecall seed remove {result.Pack}` to remove the pack");

        PrintNotice(output, notice, services.GetRequiredService<AgentRecallOptions>().ResolvedActivityNoticeLevel);
        return 0;
    }

    private static async Task<int> SeedRemoveAsync(ISeedPackService seeds, string? pack, Dictionary<string, string> options, TextWriter output, bool json, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pack))
        {
            output.WriteLine("Usage: agentrecall seed remove <pack> [--force] [--json]");
            return 1;
        }

        SeedRemoveResult result;
        try
        {
            result = await seeds.RemoveAsync(pack, options.ContainsKey("force"), cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            output.WriteLine(ex.Message);
            return 1;
        }

        if (json)
        {
            WriteJson(output, new
            {
                pack = result.Pack,
                archived = result.Archived,
                preserved = result.Preserved,
                changes = result.Changes.Select(c => new
                {
                    key = c.Key,
                    title = c.Title,
                    ruleId = c.RuleId,
                    outcome = c.Outcome.ToString(),
                }).ToList(),
            });
            return 0;
        }

        output.WriteLine($"Removed seed pack `{result.Pack}`.");
        output.WriteLine($"- {result.Archived} rule(s) archived (a reinstall won't bring them back unless you pass --force)");
        if (result.Preserved > 0)
        {
            output.WriteLine($"- {result.Preserved} rule(s) preserved (you edited or promoted them)");
        }

        return 0;
    }

    private static async Task<int> SeedStatusAsync(ISeedPackService seeds, TextWriter output, bool json, CancellationToken cancellationToken)
    {
        var statuses = await seeds.StatusAsync(cancellationToken).ConfigureAwait(false);

        if (json)
        {
            WriteJson(output, statuses.Select(s => new
            {
                name = s.Name,
                installed = s.Installed,
                totalRules = s.TotalRules,
                active = s.Active,
                suggested = s.Suggested,
                promoted = s.Promoted,
                archived = s.Archived,
                averageConfidence = s.AverageConfidence,
            }).ToList());
            return 0;
        }

        output.WriteLine("Seed pack status:");
        foreach (var s in statuses)
        {
            output.WriteLine();
            output.WriteLine($"- {s.Name} — {(s.Installed ? "installed" : "not installed")}");
            if (!s.Installed)
            {
                output.WriteLine($"  {s.TotalRules} rules available. Install with: agentrecall seed install {s.Name}");
                continue;
            }

            output.WriteLine($"  Active: {s.Active}  Suggested: {s.Suggested}  Promoted: {s.Promoted}  Archived: {s.Archived}");
            output.WriteLine($"  Average confidence (in force): {s.AverageConfidence:0.00}");
        }

        return 0;
    }

    private static object SeedInstallJson(SeedInstallResult result) => new
    {
        pack = result.Pack,
        status = result.Status.ToString(),
        confidence = result.Confidence,
        added = result.Added,
        restored = result.Restored,
        skipped = result.Skipped,
        changes = result.Changes.Select(c => new
        {
            key = c.Key,
            title = c.Title,
            ruleId = c.RuleId,
            outcome = c.Outcome.ToString(),
        }).ToList(),
    };

    // Pending is surfaced to the user as "Suggested" for seed packs.
    private static string StatusLabel(RuleStatus status) =>
        status == RuleStatus.Pending ? "Suggested" : status.ToString();

    private static void SeedUsage(TextWriter output)
    {
        output.WriteLine("Usage:");
        output.WriteLine("  agentrecall seed list");
        output.WriteLine("  agentrecall seed show <pack>");
        output.WriteLine("  agentrecall seed install <pack> [--active] [--suggested] [--force] [--json]");
        output.WriteLine("  agentrecall seed remove <pack> [--force] [--json]");
        output.WriteLine("  agentrecall seed status [--json]");
    }
}
