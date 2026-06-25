using AgentRecall.Core;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Evaluation;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Reporting;
using AgentRecall.Core.Search;
using AgentRecall.Core.Services;
using AgentRecall.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Dna = AgentRecall.Core.Dna;

namespace AgentRecall.Cli;

/// <summary>
/// Parses command-line arguments and dispatches to the matching command.
/// Returns the process exit code.
/// </summary>
public static class CommandRouter
{
    public static async Task<int> RunAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("agentrecall");

        // No arguments behaves like `help`.
        var command = args.Length == 0 ? "help" : args[0];
        var rest = args.Length > 1 ? args[1..] : [];

        switch (command)
        {
            case "--version":
            case "-v":
            case "version":
                output.WriteLine($"{AppInfo.Name} {AppInfo.Version}");
                return 0;

            case "help":
            case "--help":
            case "-h":
                WriteHelp(output);
                return 0;

            case "init":
                return await InitAsync(services, output, logger, cancellationToken).ConfigureAwait(false);

            case "devcontainer":
                return DevcontainerInit(rest, output);

            case "setup":
                return SetupPath(output);

            case "feedback":
                return await FeedbackAsync(rest, services, output, logger, cancellationToken).ConfigureAwait(false);

            case "rules":
                return await RulesAsync(rest, services, output, logger, cancellationToken).ConfigureAwait(false);

            case "search":
                return await SearchAsync(rest, services, output, logger, cancellationToken).ConfigureAwait(false);

            case "inject-context":
                return await InjectContextAsync(rest, services, output, cancellationToken).ConfigureAwait(false);

            case "import":
                return await ImportAsync(rest, services, output, logger, cancellationToken).ConfigureAwait(false);

            case "eval":
                return await EvalAsync(rest, output, cancellationToken).ConfigureAwait(false);

            case "report":
                return await ReportAsync(rest, services, output, cancellationToken).ConfigureAwait(false);

            case "dna":
                return await DnaAsync(rest, services, output, cancellationToken).ConfigureAwait(false);

            case "outcome":
                return await OutcomeAsync(rest, services, output, cancellationToken).ConfigureAwait(false);

            case "lessons":
                return await LessonsAsync(rest, services, output, cancellationToken).ConfigureAwait(false);

            case "lifecycle":
                return await LifecycleAsync(rest, services, output, cancellationToken).ConfigureAwait(false);

            case "hook":
                return await HookAsync(rest, services, output, cancellationToken).ConfigureAwait(false);

            case "mcp":
                var server = new Mcp.McpServer(services);
                await server.RunAsync(Console.In, output, cancellationToken).ConfigureAwait(false);
                return 0;

            case "status":
                var memory = services.GetRequiredService<IMemoryService>();
                logger.LogDebug("Resolved memory service.");
                output.WriteLine(memory.Status());
                return 0;

            default:
                output.WriteLine($"Unknown command: {command}");
                output.WriteLine();
                WriteHelp(output);
                return 1;
        }
    }

    private static async Task<int> InitAsync(
        IServiceProvider services,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();

        try
        {
            var path = await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
            output.WriteLine($"Initialized AgentRecall database at: {path}");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database initialization failed.");
            output.WriteLine($"Initialization failed: {ex.Message}");
            return 1;
        }
    }

    private static int SetupPath(TextWriter output)
    {
        var result = Setup.PathSetup.Ensure();
        switch (result.Outcome)
        {
            case Setup.PathSetupOutcome.AlreadyConfigured:
                output.WriteLine($"PATH already includes {result.ToolsDirectory}. You're all set.");
                return 0;

            case Setup.PathSetupOutcome.Added:
                output.WriteLine($"Added {result.ToolsDirectory} to your {result.Detail}.");
                output.WriteLine("Open a new terminal so `agentrecall` is found automatically.");
                return 0;

            default:
                output.WriteLine($"Could not update PATH automatically: {result.Detail}");
                output.WriteLine($"Add {result.ToolsDirectory} to your PATH manually, then restart your shell.");
                return 1;
        }
    }

    private static int DevcontainerInit(string[] args, TextWriter output)
    {
        var sub = args.Length > 0 ? args[0] : string.Empty;
        if (sub != "init")
        {
            output.WriteLine("Usage: agentrecall devcontainer init [path]");
            output.WriteLine("(scaffolds dev container wiring so AgentRecall reinstalls on every rebuild)");
            return 1;
        }

        // Optional positional path; defaults to the current directory.
        var projectRoot = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
            ? args[1]
            : Directory.GetCurrentDirectory();

        try
        {
            var result = Devcontainer.DevcontainerScaffolder.Init(projectRoot);

            var verb = result.ScriptOverwritten ? "Updated" : "Wrote";
            output.WriteLine($"{verb} {result.ScriptPath} (installs AgentRecall from NuGet on container create/rebuild).");

            WriteHookOutcome(output, result.HookOutcome, result.ClaudeSettingsPath,
                "UserPromptSubmit", "automatic rule injection", Devcontainer.DevcontainerScaffolder.HookCommand);
            WriteHookOutcome(output, result.CaptureHookOutcome, result.ClaudeSettingsPath,
                "Stop", "automatic lesson capture", Devcontainer.DevcontainerScaffolder.CaptureHookCommand);

            switch (result.GuidanceOutcome)
            {
                case Devcontainer.GuidanceOutcome.Created:
                    output.WriteLine($"Wrote {result.ClaudeMdPath} with AgentRecall guidance (recall + capture accepted PR comments as Active).");
                    break;
                case Devcontainer.GuidanceOutcome.Appended:
                    output.WriteLine($"Appended AgentRecall guidance to {result.ClaudeMdPath} (recall + capture accepted PR comments as Active).");
                    break;
                case Devcontainer.GuidanceOutcome.AlreadyPresent:
                    output.WriteLine($"AgentRecall guidance already in {result.ClaudeMdPath}; left it as is.");
                    break;
            }

            if (result.CreatedDevcontainerJson)
            {
                output.WriteLine($"Created {result.DevcontainerJsonPath} wired to run it.");
                output.WriteLine("Rebuild the container to apply.");
            }
            else
            {
                output.WriteLine($"Found an existing {result.DevcontainerJsonPath}; left it untouched.");
                output.WriteLine();
                output.WriteLine(result.ManualSteps);
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Failed to scaffold dev container: {ex.Message}");
            return 1;
        }
    }

    private static void WriteHookOutcome(
        TextWriter output,
        Devcontainer.HookSetupOutcome outcome,
        string settingsPath,
        string eventName,
        string purpose,
        string command)
    {
        switch (outcome)
        {
            case Devcontainer.HookSetupOutcome.Created:
                output.WriteLine($"Wrote {settingsPath} with the {eventName} hook ({purpose}).");
                break;
            case Devcontainer.HookSetupOutcome.Merged:
                output.WriteLine($"Added the {eventName} hook to {settingsPath} ({purpose}).");
                break;
            case Devcontainer.HookSetupOutcome.AlreadyPresent:
                output.WriteLine($"{eventName} hook already present in {settingsPath}; left it as is.");
                break;
            case Devcontainer.HookSetupOutcome.SettingsUnparseable:
                output.WriteLine($"Could not parse {settingsPath}; left it untouched.");
                output.WriteLine($"Add this hook manually: a {eventName} command hook running \"{command}\".");
                break;
        }
    }

    private static async Task<int> FeedbackAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] != "add")
        {
            output.WriteLine("Usage: agentrecall feedback add --task <text> --feedback <text> [options]");
            return 1;
        }

        var options = ParseOptions(args[1..]);

        var task = options.GetValueOrDefault("task");
        var feedback = options.GetValueOrDefault("feedback");

        if (string.IsNullOrWhiteSpace(task) || string.IsNullOrWhiteSpace(feedback))
        {
            output.WriteLine("Both --task and --feedback are required.");
            output.WriteLine("Usage: agentrecall feedback add --task <text> --feedback <text> [options]");
            return 1;
        }

        var scopeLevel = ScopeLevel.Global;
        if (options.TryGetValue("scope-level", out var rawScope) &&
            !Enum.TryParse(rawScope, ignoreCase: true, out scopeLevel))
        {
            output.WriteLine($"Invalid --scope-level '{rawScope}'. Valid values: {string.Join(", ", Enum.GetNames<ScopeLevel>())}");
            return 1;
        }

        var input = new FeedbackInput
        {
            Task = task!,
            Feedback = feedback!,
            BadOutput = options.GetValueOrDefault("bad-output"),
            FixedOutput = options.GetValueOrDefault("fixed-output"),
            ScopeLevel = scopeLevel,
            ScopeValue = options.GetValueOrDefault("scope-value"),
            Tags = options.GetValueOrDefault("tags"),
            // --pending keeps the rule Pending; otherwise the configured default applies.
            AutoApprove = options.ContainsKey("pending") ? false : null,
        };

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var feedbackService = scope.ServiceProvider.GetRequiredService<IFeedbackService>();

        try
        {
            var result = await feedbackService.AddAsync(input, cancellationToken).ConfigureAwait(false);

            if (result.Rule is null)
            {
                // Skipped by the capture decision: a low-value code fact, or a duplicate.
                var reason = result.Decision?.Reason ?? result.Worthiness?.Reason ?? "not memory-worthy";
                output.WriteLine($"Not stored ({result.Decision?.Outcome.ToString() ?? "Skip"}): {reason}");
                output.WriteLine("Store a reusable lesson instead, or pass --feedback with the broader rule.");
                return 0;
            }

            if (result.Event is not null)
            {
                output.WriteLine($"Recorded feedback as event #{result.Event.Id}.");
            }

            var verb = result.ReusedExistingRule ? "Reused existing" : "Created";
            output.WriteLine($"{verb} {result.Rule.Status} rule #{result.Rule.Id}: {result.Rule.RuleText}");
            if (result.Decision is { } decision)
            {
                output.WriteLine(
                    $"Decision: {decision.Outcome} — {decision.Notice} " +
                    $"(confidence {decision.Confidence:0.00}, scope {decision.ScopeLabel}).");
            }
            if (result.Worthiness?.Verdict == Core.Memory.MemoryWorthiness.NeedsReview)
            {
                output.WriteLine("Stored the generalized lesson instead of the raw code fact.");
            }

            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record feedback.");
            output.WriteLine($"Failed to record feedback: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RulesAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sub = args.Length > 0 ? args[0] : string.Empty;

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();

        switch (sub)
        {
            case "list":
            {
                var all = await rules.ListAsync(cancellationToken).ConfigureAwait(false);
                if (all.Count == 0)
                {
                    output.WriteLine("No rules yet. Add one with: agentrecall feedback add ...");
                    return 0;
                }

                output.WriteLine($"{"ID",-4} {"STATUS",-10} {"SCOPE",-22} TRIGGER");
                foreach (var rule in all)
                {
                    var scopeText = $"{rule.ScopeLevel}:{rule.ScopeValue}";
                    output.WriteLine($"{rule.Id,-4} {rule.Status,-10} {Truncate(scopeText, 22),-22} {Truncate(rule.Trigger, 50)}");
                }

                return 0;
            }

            case "show":
            {
                if (args.Length < 2 || !int.TryParse(args[1], out var id))
                {
                    output.WriteLine("Usage: agentrecall rules show <id>");
                    return 1;
                }

                var rule = await rules.GetAsync(id, cancellationToken).ConfigureAwait(false);
                if (rule is null)
                {
                    output.WriteLine($"Rule #{id} not found.");
                    return 1;
                }

                WriteRule(output, rule);
                return 0;
            }

            case "explain":
            {
                if (args.Length < 2 || !int.TryParse(args[1], out var id))
                {
                    output.WriteLine("Usage: agentrecall rules explain <id>");
                    return 1;
                }

                var rule = await rules.GetAsync(id, cancellationToken).ConfigureAwait(false);
                if (rule is null)
                {
                    output.WriteLine($"Rule #{id} not found.");
                    return 1;
                }

                var events = await scope.ServiceProvider.GetRequiredService<IRecallEventRepository>().ListAsync(cancellationToken).ConfigureAwait(false);
                var outcomes = await scope.ServiceProvider.GetRequiredService<IRuleOutcomeRepository>().ListAsync(cancellationToken).ConfigureAwait(false);
                WriteRuleExplanation(output, rule, events, outcomes);
                return 0;
            }

            case "approve":
            case "promote":
            case "archive":
            {
                if (args.Length < 2 || !int.TryParse(args[1], out var id))
                {
                    output.WriteLine($"Usage: agentrecall rules {sub} <id>");
                    return 1;
                }

                var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();
                try
                {
                    var updated = sub switch
                    {
                        "approve" => await lifecycle.ApproveAsync(id, cancellationToken).ConfigureAwait(false),
                        "promote" => await lifecycle.PromoteAsync(id, cancellationToken).ConfigureAwait(false),
                        _ => await lifecycle.ArchiveAsync(id, cancellationToken).ConfigureAwait(false),
                    };
                    output.WriteLine($"Rule #{updated.Id} is now {updated.Status}.");
                    return 0;
                }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
                {
                    output.WriteLine(ex.Message);
                    return 1;
                }
            }

            case "supersede":
            {
                if (args.Length < 3 || !int.TryParse(args[1], out var oldId) || !int.TryParse(args[2], out var newId))
                {
                    output.WriteLine("Usage: agentrecall rules supersede <oldId> <newId>");
                    return 1;
                }

                var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();
                try
                {
                    var result = await lifecycle.SupersedeAsync(oldId, newId, cancellationToken).ConfigureAwait(false);
                    output.WriteLine($"Rule #{result.Superseded.Id} is now {result.Superseded.Status}, replaced by rule #{result.Replacement.Id} (v{result.Replacement.Version}).");
                    return 0;
                }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
                {
                    output.WriteLine(ex.Message);
                    return 1;
                }
            }

            case "conflicts":
                return await RulesConflictsAsync(args[1..], scope, output);

            default:
                output.WriteLine("Usage:");
                output.WriteLine("  agentrecall rules list");
                output.WriteLine("  agentrecall rules show <id>");
                output.WriteLine("  agentrecall rules approve <id>");
                output.WriteLine("  agentrecall rules promote <id>");
                output.WriteLine("  agentrecall rules supersede <oldId> <newId>");
                output.WriteLine("  agentrecall rules archive <id>");
                output.WriteLine("  agentrecall rules conflicts [--scope-level <level>] [--scope-value <text>] [--json]");
                output.WriteLine("  agentrecall rules explain <id>");
                return 1;
        }
    }

    // Recommendation types safe to apply in bulk via `lifecycle suggest --apply`.
    private static readonly HashSet<RecommendationType> SafeBulkApply =
        [RecommendationType.Promote, RecommendationType.RaiseConfidence, RecommendationType.LowerConfidence];

    private static async Task<int> LifecycleAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var sub = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : string.Empty;
        var options = ParseOptions(args);
        var json = options.ContainsKey("json");

        RecommendationType? typeFilter = null;
        if (options.TryGetValue("type", out var rawType))
        {
            if (!Enum.TryParse(rawType, ignoreCase: true, out RecommendationType parsed))
            {
                output.WriteLine($"Invalid --type '{rawType}'. Valid values: {string.Join(", ", Enum.GetNames<RecommendationType>())}");
                return 1;
            }

            typeFilter = parsed;
        }

        var scopeLevel = (ScopeLevel?)null;
        if (options.TryGetValue("scope-level", out var rawScope))
        {
            if (!Enum.TryParse(rawScope, ignoreCase: true, out ScopeLevel level))
            {
                output.WriteLine($"Invalid --scope-level '{rawScope}'. Valid values: {string.Join(", ", Enum.GetNames<ScopeLevel>())}");
                return 1;
            }

            scopeLevel = level;
        }

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var service = scope.ServiceProvider.GetRequiredService<Core.Lifecycle.IRuleLifecycleRecommendationService>();
        var repo = scope.ServiceProvider.GetRequiredService<IRuleLifecycleRecommendationRepository>();

        switch (sub)
        {
            case "suggest":
            {
                var query = new Core.Lifecycle.RecommendationQuery
                {
                    AsOf = DateTimeOffset.UtcNow,
                    Type = typeFilter,
                    ScopeLevel = scopeLevel,
                    ScopeValue = options.GetValueOrDefault("scope-value"),
                };

                var recs = await service.SuggestAsync(query, cancellationToken).ConfigureAwait(false);

                var applied = 0;
                if (options.ContainsKey("apply"))
                {
                    // Only low-risk types are auto-applied; archive/supersede/review need explicit apply.
                    foreach (var rec in recs.Where(r => SafeBulkApply.Contains(r.RecommendationType)))
                    {
                        await service.ApplyAsync(rec.Id, cancellationToken).ConfigureAwait(false);
                        applied++;
                    }
                }

                if (json) { WriteJson(output, recs.Select(ToRecommendationJson).ToList()); return 0; }

                output.WriteLine(options.ContainsKey("apply") ? "Lifecycle Recommendations (applying safe actions)" : "Lifecycle Recommendations (dry run — nothing changed)");
                if (recs.Count == 0)
                {
                    output.WriteLine();
                    output.WriteLine("  (no recommendations)");
                }
                else
                {
                    foreach (var rec in recs) WriteRecommendationBlock(output, rec);
                }

                if (options.ContainsKey("apply"))
                {
                    output.WriteLine();
                    output.WriteLine($"Applied {applied} safe recommendation(s). Run 'lifecycle apply <id>' for archive/supersede/review.");
                }

                return 0;
            }

            case "list":
            {
                var all = (await repo.ListAsync(cancellationToken).ConfigureAwait(false))
                    .Where(r => typeFilter is null || r.RecommendationType == typeFilter)
                    .OrderByDescending(r => r.Confidence).ThenBy(r => r.Id).ToList();
                if (json) { WriteJson(output, all.Select(ToRecommendationJson).ToList()); return 0; }

                if (all.Count == 0)
                {
                    output.WriteLine("No recommendations yet. Run: agentrecall lifecycle suggest");
                    return 0;
                }

                output.WriteLine($"{"ID",-4} {"TYPE",-16} {"STATUS",-10} {"RULE",-5} {"CONF",-5} REASON");
                foreach (var r in all)
                {
                    output.WriteLine($"{r.Id,-4} {r.RecommendationType,-16} {r.Status,-10} #{r.RuleId,-4} {r.Confidence,-5:0.00} {Truncate(r.Reason, 50)}");
                }

                return 0;
            }

            case "show":
            {
                if (args.Length < 2 || !int.TryParse(args[1], out var id))
                {
                    output.WriteLine("Usage: agentrecall lifecycle show <id> [--json]");
                    return 1;
                }

                var rec = await repo.GetAsync(id, cancellationToken).ConfigureAwait(false);
                if (rec is null)
                {
                    output.WriteLine($"Recommendation #{id} not found.");
                    return 1;
                }

                if (json) { WriteJson(output, ToRecommendationJson(rec)); return 0; }

                WriteRecommendationBlock(output, rec);
                output.WriteLine($"  Status:   {rec.Status}");
                if (!string.IsNullOrWhiteSpace(rec.RejectedReason))
                {
                    output.WriteLine($"  Rejected: {rec.RejectedReason}");
                }

                return 0;
            }

            case "apply":
            {
                if (args.Length < 2 || !int.TryParse(args[1], out var id))
                {
                    output.WriteLine("Usage: agentrecall lifecycle apply <id>");
                    return 1;
                }

                try
                {
                    var rec = await service.ApplyAsync(id, cancellationToken).ConfigureAwait(false);
                    if (rec is null)
                    {
                        output.WriteLine($"Recommendation #{id} not found.");
                        return 1;
                    }

                    output.WriteLine($"Recommendation #{rec.Id} ({rec.RecommendationType}) is now {rec.Status}.");
                    return 0;
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
                {
                    output.WriteLine($"Could not apply recommendation #{id}: {ex.Message}");
                    return 1;
                }
            }

            case "reject":
            {
                if (args.Length < 2 || !int.TryParse(args[1], out var id))
                {
                    output.WriteLine("Usage: agentrecall lifecycle reject <id> --reason \"<reason>\"");
                    return 1;
                }

                var rec = await service.RejectAsync(id, options.GetValueOrDefault("reason") ?? string.Empty, cancellationToken).ConfigureAwait(false);
                if (rec is null)
                {
                    output.WriteLine($"Recommendation #{id} not found.");
                    return 1;
                }

                output.WriteLine($"Rejected recommendation #{rec.Id}. It won't be proposed again.");
                return 0;
            }

            default:
                output.WriteLine("Usage:");
                output.WriteLine("  agentrecall lifecycle suggest [--type <t>] [--scope-level <l>] [--scope-value <v>] [--apply] [--json]");
                output.WriteLine("  agentrecall lifecycle list [--type <t>] [--json]");
                output.WriteLine("  agentrecall lifecycle show <id> [--json]");
                output.WriteLine("  agentrecall lifecycle apply <id>");
                output.WriteLine("  agentrecall lifecycle reject <id> --reason \"<reason>\"");
                return 1;
        }
    }

    private static void WriteRecommendationBlock(TextWriter output, RuleLifecycleRecommendation r)
    {
        output.WriteLine();
        var target = r.TargetRuleId is { } t ? $" with #{t}" : string.Empty;
        output.WriteLine($"#{r.Id} {r.RecommendationType} rule #{r.RuleId}{target}");
        output.WriteLine($"  Reason:     {r.Reason}");
        output.WriteLine($"  Evidence:   {r.Evidence}");
        output.WriteLine($"  Confidence: {r.Confidence:0.00}");
    }

    private static object ToRecommendationJson(RuleLifecycleRecommendation r) => new
    {
        id = r.Id,
        ruleId = r.RuleId,
        targetRuleId = r.TargetRuleId,
        type = r.RecommendationType.ToString(),
        reason = r.Reason,
        evidence = r.Evidence,
        confidence = r.Confidence,
        status = r.Status.ToString(),
        rejectedReason = r.RejectedReason,
    };

    private static async Task<int> LessonsAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var sub = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : string.Empty;
        var options = ParseOptions(args);
        var json = options.ContainsKey("json");

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var mining = scope.ServiceProvider.GetRequiredService<Core.Mining.ILessonMiningService>();
        var candidates = scope.ServiceProvider.GetRequiredService<ILessonCandidateRepository>();

        switch (sub)
        {
            case "mine":
            {
                var result = await mining.MineAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (json) { WriteJson(output, result.Suggested.Select(ToCandidateJson).ToList()); return 0; }

                output.WriteLine("Suggested Lessons");
                if (result.Suggested.Count == 0)
                {
                    output.WriteLine();
                    output.WriteLine("  (no repeated patterns found above the threshold)");
                }
                else
                {
                    foreach (var c in result.Suggested)
                    {
                        WriteCandidateBlock(output, c);
                    }
                }

                output.WriteLine();
                output.WriteLine($"({result.Created} new, {result.Updated} updated, {result.SuppressedByRule} covered by existing rules, {result.SuppressedByRejection} previously rejected)");
                return 0;
            }

            case "list":
            {
                var all = (await candidates.ListAsync(cancellationToken).ConfigureAwait(false))
                    .OrderByDescending(c => c.Confidence).ThenBy(c => c.Id).ToList();
                if (json) { WriteJson(output, all.Select(ToCandidateJson).ToList()); return 0; }

                if (all.Count == 0)
                {
                    output.WriteLine("No lesson candidates yet. Run: agentrecall lessons mine");
                    return 0;
                }

                output.WriteLine($"{"ID",-4} {"STATUS",-10} {"OCC",-4} {"CONF",-5} TITLE");
                foreach (var c in all)
                {
                    output.WriteLine($"{c.Id,-4} {c.Status,-10} {c.OccurrenceCount,-4} {c.Confidence,-5:0.00} {Truncate(c.Title, 60)}");
                }

                return 0;
            }

            case "show":
            {
                if (args.Length < 2 || !int.TryParse(args[1], out var id))
                {
                    output.WriteLine("Usage: agentrecall lessons show <id> [--json]");
                    return 1;
                }

                var candidate = await candidates.GetAsync(id, cancellationToken).ConfigureAwait(false);
                if (candidate is null)
                {
                    output.WriteLine($"Lesson candidate #{id} not found.");
                    return 1;
                }

                if (json) { WriteJson(output, ToCandidateJson(candidate)); return 0; }

                WriteCandidateBlock(output, candidate);
                output.WriteLine($"  Status:      {candidate.Status}");
                if (!string.IsNullOrWhiteSpace(candidate.RejectedReason))
                {
                    output.WriteLine($"  Rejected:    {candidate.RejectedReason}");
                }

                output.WriteLine($"  First seen:  {candidate.FirstSeenAt:u}");
                output.WriteLine($"  Last seen:   {candidate.LastSeenAt:u}");
                return 0;
            }

            case "accept":
            {
                if (args.Length < 2 || !int.TryParse(args[1], out var id))
                {
                    output.WriteLine("Usage: agentrecall lessons accept <id>");
                    return 1;
                }

                var candidate = await mining.AcceptAsync(id, cancellationToken).ConfigureAwait(false);
                if (candidate is null)
                {
                    output.WriteLine($"Lesson candidate #{id} not found.");
                    return 1;
                }

                output.WriteLine($"Accepted lesson candidate #{candidate.Id}; created an Active rule from it.");
                output.WriteLine($"  {candidate.SuggestedRule}");
                return 0;
            }

            case "reject":
            {
                if (args.Length < 2 || !int.TryParse(args[1], out var id))
                {
                    output.WriteLine("Usage: agentrecall lessons reject <id> --reason \"<reason>\"");
                    return 1;
                }

                var reason = options.GetValueOrDefault("reason") ?? string.Empty;
                var candidate = await mining.RejectAsync(id, reason, cancellationToken).ConfigureAwait(false);
                if (candidate is null)
                {
                    output.WriteLine($"Lesson candidate #{id} not found.");
                    return 1;
                }

                output.WriteLine($"Rejected lesson candidate #{candidate.Id}. Its pattern won't be proposed again.");
                output.WriteLine($"  Reason: {candidate.RejectedReason}");
                return 0;
            }

            default:
                output.WriteLine("Usage:");
                output.WriteLine("  agentrecall lessons mine [--json]");
                output.WriteLine("  agentrecall lessons list [--json]");
                output.WriteLine("  agentrecall lessons show <id> [--json]");
                output.WriteLine("  agentrecall lessons accept <id>");
                output.WriteLine("  agentrecall lessons reject <id> --reason \"<reason>\"");
                return 1;
        }
    }

    private static void WriteCandidateBlock(TextWriter output, Core.Domain.LessonCandidate c)
    {
        output.WriteLine();
        output.WriteLine($"#{c.Id} {c.Title}");
        output.WriteLine($"  Rule:        {c.SuggestedRule}");
        output.WriteLine($"  Category:    {c.Category}");
        output.WriteLine($"  Occurrences: {c.OccurrenceCount}");
        output.WriteLine($"  Confidence:  {c.Confidence:0.00}");
        output.WriteLine($"  Supporting events: {c.SupportingEventIds}");
    }

    private static object ToCandidateJson(Core.Domain.LessonCandidate c) => new
    {
        id = c.Id,
        title = c.Title,
        suggestedRule = c.SuggestedRule,
        category = c.Category.ToString(),
        status = c.Status.ToString(),
        occurrenceCount = c.OccurrenceCount,
        confidence = c.Confidence,
        supportingEventIds = c.SupportingEventIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse).ToArray(),
        firstSeenAt = c.FirstSeenAt,
        lastSeenAt = c.LastSeenAt,
        normalizedKey = c.NormalizedKey,
        rejectedReason = c.RejectedReason,
    };

    private static async Task<int> OutcomeAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] != "record")
        {
            output.WriteLine("Usage: agentrecall outcome record --type <type> [--rule-id <id>] [--retrieval-id <id>] [--reason <text>] [--allow-duplicate]");
            output.WriteLine($"Types: {string.Join(", ", Enum.GetNames<OutcomeType>())}");
            return 1;
        }

        var options = ParseOptions(args[1..]);

        if (!options.TryGetValue("type", out var rawType) || !Enum.TryParse<OutcomeType>(rawType, ignoreCase: true, out var type))
        {
            output.WriteLine($"--type is required. Valid values: {string.Join(", ", Enum.GetNames<OutcomeType>())}");
            return 1;
        }

        int? ruleId = null;
        if (options.TryGetValue("rule-id", out var rawRuleId))
        {
            if (!int.TryParse(rawRuleId, out var parsed))
            {
                output.WriteLine($"Invalid --rule-id '{rawRuleId}'. Expected an integer.");
                return 1;
            }

            ruleId = parsed;
        }

        var retrievalId = options.GetValueOrDefault("retrieval-id");
        if (ruleId is null && string.IsNullOrWhiteSpace(retrievalId))
        {
            output.WriteLine("Provide --rule-id or --retrieval-id.");
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var tracker = scope.ServiceProvider.GetRequiredService<Core.Outcomes.IOutcomeTrackingService>();

        var result = await tracker.RecordAsync(new Core.Outcomes.OutcomeRequest
        {
            RuleId = ruleId,
            RetrievalId = retrievalId,
            Type = type,
            Reason = options.GetValueOrDefault("reason"),
            AllowDuplicate = options.ContainsKey("allow-duplicate"),
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Enabled)
        {
            output.WriteLine("Outcome tracking is disabled (set AgentRecall:OutcomeTrackingEnabled to true).");
            return 0;
        }

        if (result.Error is not null)
        {
            output.WriteLine(result.Error);
            return 1;
        }

        if (result.Adjustments.Count == 0)
        {
            var note = result.SkippedDuplicates > 0
                ? $"No change: {result.SkippedDuplicates} duplicate outcome(s) skipped."
                : "No matching rules to adjust.";
            output.WriteLine(note);
            return 0;
        }

        foreach (var adjustment in result.Adjustments)
        {
            output.WriteLine(
                $"Rule #{adjustment.RuleId}: {adjustment.Type} {adjustment.PreviousConfidence:0.00} -> {adjustment.NewConfidence:0.00} ({adjustment.Delta:+0.00;-0.00;0}). {adjustment.Reason}");
        }

        if (result.SkippedDuplicates > 0)
        {
            output.WriteLine($"({result.SkippedDuplicates} duplicate outcome(s) skipped.)");
        }

        return 0;
    }

    private static async Task<int> RulesConflictsAsync(string[] args, AsyncServiceScope scope, TextWriter output)
    {
        var options = ParseOptions(args);
        var json = options.ContainsKey("json");

        var rulesRepo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var detector = scope.ServiceProvider.GetRequiredService<Core.Conflicts.IRuleConflictDetector>();
        var resolver = scope.ServiceProvider.GetRequiredService<Core.Conflicts.IRuleResolutionService>();

        // Only in-force rules can actually clash in practice.
        IEnumerable<RecallRule> pool = (await rulesRepo.ListAsync().ConfigureAwait(false))
            .Where(r => !r.Deprecated && r.Status is RuleStatus.Active or RuleStatus.Promoted);

        if (options.TryGetValue("scope-level", out var rawLevel))
        {
            if (!Enum.TryParse<ScopeLevel>(rawLevel, ignoreCase: true, out var level))
            {
                output.WriteLine($"Invalid --scope-level '{rawLevel}'. Valid values: {string.Join(", ", Enum.GetNames<ScopeLevel>())}");
                return 1;
            }

            pool = pool.Where(r => r.ScopeLevel == level);
        }

        if (options.TryGetValue("scope-value", out var scopeValue) && !string.IsNullOrWhiteSpace(scopeValue))
        {
            pool = pool.Where(r => string.Equals(r.ScopeValue, scopeValue, StringComparison.OrdinalIgnoreCase));
        }

        var rules = pool.ToList();
        var byId = rules.ToDictionary(r => r.Id);

        var items = detector.Detect(rules)
            .Select(conflict =>
            {
                var members = conflict.RuleIds.Select(id => byId[id]).ToList();
                return (Conflict: conflict, Resolution: resolver.Resolve(members));
            })
            .ToList();

        if (json)
        {
            var payload = items.Select(it => new
            {
                conflictId = it.Conflict.ConflictId,
                conflictType = it.Conflict.ConflictType.ToString(),
                ruleIds = it.Conflict.RuleIds,
                summary = it.Conflict.Summary,
                detectedReason = it.Conflict.DetectedReason,
                selectedRuleId = it.Resolution.SelectedRuleId,
                ignoredRuleIds = it.Resolution.IgnoredRuleIds,
                explanation = it.Resolution.Explanation,
                confidence = it.Resolution.Confidence,
            }).ToList();
            WriteJson(output, payload);
            return 0;
        }

        if (items.Count == 0)
        {
            output.WriteLine("No conflicts detected.");
            return 0;
        }

        output.WriteLine($"{items.Count} conflict(s) detected:");
        foreach (var it in items)
        {
            var selected = byId[it.Resolution.SelectedRuleId];
            output.WriteLine();
            output.WriteLine($"[{it.Conflict.ConflictType}] rules {string.Join(", ", it.Conflict.RuleIds.Select(id => $"#{id}"))} — {it.Conflict.Summary}");
            output.WriteLine($"  Selected: #{selected.Id} {Truncate(selected.RuleText, 80)}");
            output.WriteLine($"  Why: {string.Join("; ", it.Resolution.Explanation)}");
            output.WriteLine($"  Ignored: {string.Join(", ", it.Resolution.IgnoredRuleIds.Select(id => $"#{id}"))}");
        }

        return 0;
    }

    private static async Task<int> ImportAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sub = args.Length > 0 ? args[0] : string.Empty;

        if (sub == "pr-comments")
        {
            return await ImportPrCommentsAsync(args[1..], services, output, cancellationToken).ConfigureAwait(false);
        }

        var kind = sub switch
        {
            "build-log" => (LogKind?)LogKind.Build,
            "test-log" => LogKind.Test,
            "lint-log" => LogKind.Lint,
            _ => null,
        };

        if (kind is null || args.Length < 2)
        {
            output.WriteLine("Usage:");
            output.WriteLine("  agentrecall import build-log <file>");
            output.WriteLine("  agentrecall import test-log <file>");
            output.WriteLine("  agentrecall import lint-log <file>");
            output.WriteLine("  agentrecall import pr-comments <file> [--task <pr title>] [--scope-level <level>] [--scope-value <text>] [--tags <a,b>] [--accepted]");
            return 1;
        }

        var filePath = args[1];

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var importer = scope.ServiceProvider.GetRequiredService<ILogImportService>();

        try
        {
            var result = await importer.ImportAsync(kind.Value, filePath, cancellationToken).ConfigureAwait(false);
            output.WriteLine($"Imported {result.Kind} log: {result.FailuresFound} failure(s), {result.EventsCreated} event(s) created.");
            if (result.RulesReinforced > 0)
            {
                output.WriteLine($"Reinforced {result.RulesReinforced} rule(s); {result.RulesPromoted} auto-promoted.");
            }

            return 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            output.WriteLine($"Import failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ImportPrCommentsAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        // The file is the first positional argument; flags follow.
        var filePath = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            output.WriteLine("Usage: agentrecall import pr-comments <file> [--task <pr title>] [--scope-level <level>] [--scope-value <text>] [--tags <a,b>] [--accepted]");
            return 1;
        }

        var options = ParseOptions(args);

        var scopeLevel = ScopeLevel.Global;
        if (options.TryGetValue("scope-level", out var rawScope) &&
            !Enum.TryParse(rawScope, ignoreCase: true, out scopeLevel))
        {
            output.WriteLine($"Invalid --scope-level '{rawScope}'. Valid values: {string.Join(", ", Enum.GetNames<ScopeLevel>())}");
            return 1;
        }

        var importOptions = new PullRequestImportOptions
        {
            PullRequestTitle = options.GetValueOrDefault("task"),
            ScopeLevel = scopeLevel,
            ScopeValue = options.GetValueOrDefault("scope-value"),
            Tags = options.GetValueOrDefault("tags"),
            // --accepted records the comments as Active rules (the user acted on them).
            Accepted = options.ContainsKey("accepted"),
        };

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var importer = scope.ServiceProvider.GetRequiredService<IPullRequestImportService>();

        try
        {
            var result = await importer.ImportFileAsync(filePath, importOptions, cancellationToken).ConfigureAwait(false);
            var statusWord = importOptions.Accepted ? "active" : "pending";
            output.WriteLine($"Imported PR comments: {result.CommentsFound} comment(s), {result.RulesCreated} {statusWord} rule(s) created, {result.Skipped} skipped.");
            if (result.RuleIds.Count > 0)
            {
                output.WriteLine($"Created rule(s): {string.Join(", ", result.RuleIds.Select(id => $"#{id}"))}. Review with: agentrecall rules list");
            }

            return 0;
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            output.WriteLine($"Import failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> InjectContextAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var task = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : null;
        if (string.IsNullOrWhiteSpace(task))
        {
            output.WriteLine("Usage: agentrecall inject-context \"<task>\" [--scope-level <level>] [--scope-value <text>] [--file-path <path>] [--limit <n>] [--include-pending]");
            return 1;
        }

        var options = ParseOptions(args);

        var scopeLevel = (ScopeLevel?)null;
        if (options.TryGetValue("scope-level", out var rawScope))
        {
            if (!Enum.TryParse<ScopeLevel>(rawScope, ignoreCase: true, out var level))
            {
                output.WriteLine($"Invalid --scope-level '{rawScope}'. Valid values: {string.Join(", ", Enum.GetNames<ScopeLevel>())}");
                return 1;
            }

            scopeLevel = level;
        }

        var request = new ContextRequest
        {
            Task = task!,
            ScopeLevel = scopeLevel,
            ScopeValue = options.GetValueOrDefault("scope-value"),
            FileNames = options.TryGetValue("file-path", out var filePath) && !string.IsNullOrWhiteSpace(filePath) ? [filePath] : [],
            IncludePending = options.ContainsKey("include-pending"),
            // This is a real retrieval, so record it for the learning reports.
            RecordUsage = true,
        };

        if (options.TryGetValue("limit", out var rawLimit))
        {
            if (!int.TryParse(rawLimit, out var limit) || limit <= 0)
            {
                output.WriteLine($"Invalid --limit '{rawLimit}'. Expected a positive integer.");
                return 1;
            }

            request = request with { Limit = limit };
        }

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var service = scope.ServiceProvider.GetRequiredService<IContextInjectionService>();

        var result = await service.BuildContextAsync(request, cancellationToken).ConfigureAwait(false);

        if (!result.All.Any())
        {
            output.WriteLine($"No relevant rules for \"{task}\".");
            return 0;
        }

        output.WriteLine(result.Explanation);

        // Every bucket is rendered in the same conditional shape rules are stored in.
        WriteBucket(output, "Must Follow", result.MustFollow);
        WriteBucket(output, "Warnings", result.Warnings);
        WriteBucket(output, "Preferred Patterns", result.Suggested);

        var ids = ContextProjection.SourceRuleIds(result);
        output.WriteLine($"Source rule IDs: {string.Join(", ", ids.Select(id => $"#{id}"))}");

        // Only when conflict resolution actually changed what was injected.
        if (result.Conflicts.Count > 0)
        {
            output.WriteLine();
            output.WriteLine(Core.Conflicts.ConflictRenderer.Hint);
            output.WriteLine();
            output.WriteLine(Core.Conflicts.ConflictRenderer.Section(result.Conflicts));
        }

        return 0;
    }

    private static void WriteBucket(TextWriter output, string title, IReadOnlyList<Core.Context.InjectedRule> rules)
    {
        if (rules.Count == 0)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine($"{title}:");
        foreach (var injected in rules)
        {
            // Render each rule as a conditional block: When … / Do / Avoid / Because / Source.
            var block = Core.Context.ConditionalRuleFormatter.Format(injected.Rule, indent: 4, includeSource: true);
            var lines = block.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
            output.WriteLine($"- {lines[0]}");
            for (var i = 1; i < lines.Length; i++)
            {
                output.WriteLine(lines[i]);
            }
        }
    }

    private static async Task<int> SearchAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // The query is the first positional argument; flags follow.
        var query = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0]
            : null;

        if (string.IsNullOrWhiteSpace(query))
        {
            output.WriteLine("Usage: agentrecall search \"<query>\" [--scope-level <level>] [--scope-value <text>] [--limit <n>]");
            return 1;
        }

        var options = ParseOptions(args);

        var searchOptions = new SearchOptions();

        if (options.TryGetValue("scope-level", out var rawScope))
        {
            if (!Enum.TryParse<ScopeLevel>(rawScope, ignoreCase: true, out var level))
            {
                output.WriteLine($"Invalid --scope-level '{rawScope}'. Valid values: {string.Join(", ", Enum.GetNames<ScopeLevel>())}");
                return 1;
            }

            searchOptions = searchOptions with { ScopeLevel = level };
        }

        if (options.TryGetValue("scope-value", out var scopeValue))
        {
            searchOptions = searchOptions with { ScopeValue = scopeValue };
        }

        if (options.TryGetValue("limit", out var rawLimit))
        {
            if (!int.TryParse(rawLimit, out var limit) || limit <= 0)
            {
                output.WriteLine($"Invalid --limit '{rawLimit}'. Expected a positive integer.");
                return 1;
            }

            searchOptions = searchOptions with { Limit = limit };
        }

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var search = scope.ServiceProvider.GetRequiredService<IRecallSearchService>();

        try
        {
            var results = await search.SearchAsync(query, searchOptions, cancellationToken).ConfigureAwait(false);
            if (results.Count == 0)
            {
                output.WriteLine($"No matching rules for \"{query}\".");
                return 0;
            }

            output.WriteLine($"{results.Count} result(s) for \"{query}\":");
            foreach (var result in results)
            {
                var rule = result.Rule;
                output.WriteLine($"  #{rule.Id} [{rule.Status}] score={result.Score:0.00} conf={rule.Confidence:0.00} {rule.ScopeLevel}:{rule.ScopeValue}");
                output.WriteLine($"      {Truncate(rule.RuleText, 90)}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Search failed.");
            output.WriteLine($"Search failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> HookAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var sub = args.Length > 0 ? args[0] : string.Empty;

        switch (sub)
        {
            case "user-prompt-submit":
            {
                var payload = await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var context = await Hooks.UserPromptSubmitHook
                    .RunAsync(payload, services, Console.Error, cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrEmpty(context))
                {
                    output.WriteLine(context);
                }

                // Always succeed so the hook never blocks the prompt.
                return 0;
            }

            case "capture":
            {
                var payload = await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var message = await Hooks.CaptureHook
                    .RunAsync(payload, services, Console.Error, cancellationToken)
                    .ConfigureAwait(false);

                // Surface a captured/skipped decision as a non-blocking Stop-hook
                // systemMessage. Nothing is written when there was no decision.
                if (!string.IsNullOrEmpty(message))
                {
                    output.WriteLine(new System.Text.Json.Nodes.JsonObject
                    {
                        ["systemMessage"] = message,
                    }.ToJsonString());
                }

                // Always succeed so the hook never blocks Claude Code.
                return 0;
            }

            default:
                output.WriteLine("Usage: agentrecall hook <user-prompt-submit|capture>");
                output.WriteLine("(reads the Claude Code hook payload on stdin; user-prompt-submit injects");
                output.WriteLine(" recall context, capture stores reusable lessons after a turn)");
                return 1;
        }
    }

    private static async Task<int> EvalAsync(string[] args, TextWriter output, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] != "retrieval")
        {
            output.WriteLine("Usage: agentrecall eval retrieval [--dataset <path>]");
            return 1;
        }

        var options = ParseOptions(args);

        EvaluationDataset dataset;
        try
        {
            dataset = options.TryGetValue("dataset", out var path) && !string.IsNullOrWhiteSpace(path)
                ? EvaluationDatasetLoader.LoadFile(path)
                : EvaluationDatasetLoader.LoadDefault();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            output.WriteLine($"Failed to load evaluation dataset: {ex.Message}");
            return 1;
        }

        // Evaluate against an isolated, throwaway store so the user's real DB is
        // never touched or polluted.
        var tempDirectory = Path.Combine(Path.GetTempPath(), "agentrecall-eval", Guid.NewGuid().ToString("N"));
        var evalOptions = new AgentRecallOptions { DataDirectory = tempDirectory, DatabaseFileName = "eval.db" };

        var collection = new ServiceCollection();
        collection.AddSingleton(evalOptions);
        collection.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        collection.AddAgentRecallPersistence();

        await using var provider = collection.BuildServiceProvider();
        try
        {
            RetrievalEvaluationReport report;
            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);
                var rules = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
                var search = scope.ServiceProvider.GetRequiredService<IRecallSearchService>();
                report = await RetrievalEvaluationHarness.RunAsync(dataset, rules, search, cancellationToken).ConfigureAwait(false);
            }

            WriteEvaluationReport(output, report);
            return report.Passed ? 0 : 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of the throwaway store.
            }
        }
    }

    private static void WriteEvaluationReport(TextWriter output, RetrievalEvaluationReport report)
    {
        var m = report.Metrics;
        var b = report.Baseline;

        output.WriteLine($"Retrieval evaluation over {m.ScenarioCount} scenario(s):");
        output.WriteLine($"  Precision@1: {m.PrecisionAt1:0.000}  (baseline {b.PrecisionAt1:0.000})");
        output.WriteLine($"  Precision@3: {m.PrecisionAt3:0.000}  (baseline {b.PrecisionAt3:0.000})");
        output.WriteLine($"  Recall@5:    {m.RecallAt5:0.000}  (baseline {b.RecallAt5:0.000})");

        // Surface scenarios where the expected rule wasn't retrieved in the top 5.
        var misses = report.Scenarios.Where(s => s.RecallAt5 < 1.0).ToList();
        if (misses.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"Misses ({misses.Count}):");
            foreach (var miss in misses)
            {
                var ranked = miss.RankedTopK.Count > 0 ? string.Join(", ", miss.RankedTopK) : "(none)";
                output.WriteLine($"  \"{miss.Query}\" expected [{string.Join(", ", miss.Expected)}] but got [{ranked}]");
            }
        }

        output.WriteLine();
        if (report.Passed)
        {
            output.WriteLine("PASS: retrieval quality meets the baseline.");
        }
        else
        {
            output.WriteLine("FAIL: retrieval quality dropped below the baseline.");
            foreach (var failure in report.Failures)
            {
                output.WriteLine($"  - {failure}");
            }
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // This output goes to a terminal, not a browser, so emit rule text such as
        // "Result<T>" literally rather than HTML-escaping the angle brackets.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static async Task<int> ReportAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var sub = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : string.Empty;
        var options = ParseOptions(args);
        var json = options.ContainsKey("json");

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var reports = scope.ServiceProvider.GetRequiredService<ILearningReportService>();

        switch (sub)
        {
            case "monthly":
            {
                var now = DateTimeOffset.UtcNow;
                var year = now.Year;
                var month = now.Month;
                if (options.TryGetValue("month", out var rawMonth))
                {
                    if (!TryParseMonth(rawMonth, out year, out month))
                    {
                        output.WriteLine($"Invalid --month '{rawMonth}'. Expected YYYY-MM, e.g. 2026-06.");
                        return 1;
                    }
                }

                var report = await reports.GetMonthlyReportAsync(year, month, cancellationToken).ConfigureAwait(false);
                if (json) { WriteJson(output, report); return 0; }
                WriteMonthlyReport(output, report);
                return 0;
            }

            case "lifecycle":
            {
                var report = await reports.GetLifecycleReportAsync(cancellationToken).ConfigureAwait(false);
                if (json) { WriteJson(output, report); return 0; }
                WriteLifecycleReport(output, report);
                return 0;
            }

            case "usage":
            {
                var usageOptions = new UsageReportOptions { AsOf = DateTimeOffset.UtcNow };
                if (options.TryGetValue("top", out var rawTop))
                {
                    if (!int.TryParse(rawTop, out var top) || top <= 0)
                    {
                        output.WriteLine($"Invalid --top '{rawTop}'. Expected a positive integer.");
                        return 1;
                    }

                    usageOptions = usageOptions with { Top = top };
                }

                if (options.TryGetValue("stale-days", out var rawStale))
                {
                    if (!int.TryParse(rawStale, out var staleDays) || staleDays < 0)
                    {
                        output.WriteLine($"Invalid --stale-days '{rawStale}'. Expected a non-negative integer.");
                        return 1;
                    }

                    usageOptions = usageOptions with { StaleDays = staleDays };
                }

                var report = await reports.GetUsageReportAsync(usageOptions, cancellationToken).ConfigureAwait(false);
                if (json) { WriteJson(output, report); return 0; }
                WriteUsageReport(output, report);
                return 0;
            }

            case "dna":
            {
                var top = 5;
                if (options.TryGetValue("top", out var rawTop))
                {
                    if (!int.TryParse(rawTop, out top) || top <= 0)
                    {
                        output.WriteLine($"Invalid --top '{rawTop}'. Expected a positive integer.");
                        return 1;
                    }
                }

                var report = await reports.GetDnaReportAsync(top, cancellationToken).ConfigureAwait(false);
                if (json) { WriteJson(output, report); return 0; }
                WriteDnaReport(output, report);
                return 0;
            }

            case "lifecycle-recommendations":
            {
                var all = await scope.ServiceProvider
                    .GetRequiredService<IRuleLifecycleRecommendationRepository>()
                    .ListAsync(cancellationToken).ConfigureAwait(false);

                // type -> per-status counts, deterministic order.
                var byType = Enum.GetValues<RecommendationType>().ToDictionary(
                    t => t,
                    t => new
                    {
                        Suggested = all.Count(r => r.RecommendationType == t && r.Status == RecommendationStatus.Suggested),
                        Applied = all.Count(r => r.RecommendationType == t && r.Status == RecommendationStatus.Applied),
                        Accepted = all.Count(r => r.RecommendationType == t && r.Status == RecommendationStatus.Accepted),
                        Rejected = all.Count(r => r.RecommendationType == t && r.Status == RecommendationStatus.Rejected),
                    });

                if (json)
                {
                    WriteJson(output, new
                    {
                        suggested = all.Count(r => r.Status == RecommendationStatus.Suggested),
                        applied = all.Count(r => r.Status == RecommendationStatus.Applied),
                        accepted = all.Count(r => r.Status == RecommendationStatus.Accepted),
                        rejected = all.Count(r => r.Status == RecommendationStatus.Rejected),
                        byType = byType.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                    });
                    return 0;
                }

                output.WriteLine("Lifecycle Recommendations");
                output.WriteLine();
                output.WriteLine($"  Suggested: {all.Count(r => r.Status == RecommendationStatus.Suggested)}  |  Applied: {all.Count(r => r.Status == RecommendationStatus.Applied)}  |  Accepted: {all.Count(r => r.Status == RecommendationStatus.Accepted)}  |  Rejected: {all.Count(r => r.Status == RecommendationStatus.Rejected)}");
                output.WriteLine();
                output.WriteLine("  By type (suggested):");
                foreach (var (type, counts) in byType)
                {
                    output.WriteLine($"    {type,-16} {counts.Suggested}");
                }

                return 0;
            }

            default:
                output.WriteLine("Usage:");
                output.WriteLine("  agentrecall report monthly [--month YYYY-MM] [--json]");
                output.WriteLine("  agentrecall report lifecycle [--json]");
                output.WriteLine("  agentrecall report usage [--top <n>] [--stale-days <n>] [--json]");
                output.WriteLine("  agentrecall report dna [--top <n>] [--json]");
                output.WriteLine("  agentrecall report lifecycle-recommendations [--json]");
                return 1;
        }
    }

    private static void WriteJson<T>(TextWriter output, T report) =>
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, ReportJsonOptions));

    private static bool TryParseMonth(string raw, out int year, out int month)
    {
        year = 0;
        month = 0;
        var parts = raw.Split('-');
        return parts.Length == 2
            && int.TryParse(parts[0], out year)
            && int.TryParse(parts[1], out month)
            && month is >= 1 and <= 12;
    }

    private static void WriteMonthlyReport(TextWriter output, MonthlyLearningReport r)
    {
        output.WriteLine("AgentRecall Learning Report");
        output.WriteLine($"Period: {r.Period}");
        output.WriteLine();
        output.WriteLine($"Lessons Captured:            {r.LessonsCaptured}");
        output.WriteLine($"Lessons Promoted:            {r.LessonsPromoted}");
        output.WriteLine($"Lessons Superseded:          {r.LessonsSuperseded}");
        output.WriteLine($"Lessons Rejected:            {r.LessonsRejected}");
        output.WriteLine($"Frequently Used Rules:       {r.FrequentlyUsedRules}");
        output.WriteLine($"Average Confidence:          {r.AverageConfidence:0.00}");
        output.WriteLine($"Most Retrieved Rule:         {(r.MostRetrievedRule is null ? "(none)" : $"\"{r.MostRetrievedRule.RuleText}\" ({r.MostRetrievedRule.RetrievalCount}x)")}");
        output.WriteLine($"Most Common Lesson Category: {r.MostCommonCategory ?? "(none)"}");
        output.WriteLine($"Positive Outcomes:           {r.PositiveOutcomes}");
        output.WriteLine($"Negative Outcomes:           {r.NegativeOutcomes}");
        output.WriteLine($"Net Confidence Change:       {r.NetConfidenceChange:+0.00;-0.00;0.00}");

        if (r.MostImprovedRules.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Most Improved Rules:");
            foreach (var rule in r.MostImprovedRules)
            {
                output.WriteLine($"  #{rule.RuleId} {Truncate(rule.RuleText, 70)} ({rule.NetConfidenceChange:+0.00;-0.00;0.00})");
            }
        }

        if (r.MostDegradedRules.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Most Degraded Rules:");
            foreach (var rule in r.MostDegradedRules)
            {
                output.WriteLine($"  #{rule.RuleId} {Truncate(rule.RuleText, 70)} ({rule.NetConfidenceChange:+0.00;-0.00;0.00})");
            }
        }
    }

    private static void WriteLifecycleReport(TextWriter output, RuleLifecycleReport r)
    {
        output.WriteLine("Rule Lifecycle");
        output.WriteLine();
        output.WriteLine($"Created:      {r.Created}");
        output.WriteLine($"Promoted:     {r.Promoted}");
        output.WriteLine($"Superseded:   {r.Superseded}");
        output.WriteLine($"Archived:     {r.Archived}");
        output.WriteLine($"Rejected:     {r.Rejected}");
        output.WriteLine($"Still Active: {r.StillActive}");
    }

    private static void WriteUsageReport(TextWriter output, LearningUsageReport r)
    {
        output.WriteLine("Top Retrieved Rules");
        output.WriteLine();
        if (r.TopRetrievedRules.Count == 0)
        {
            output.WriteLine("  (no retrievals recorded yet)");
        }
        else
        {
            var rank = 1;
            foreach (var rule in r.TopRetrievedRules)
            {
                output.WriteLine($"  {rank}. {Truncate(rule.RuleText, 80)}");
                output.WriteLine($"     Retrieved: {rule.RetrievalCount} times");
                rank++;
            }
        }

        output.WriteLine();
        output.WriteLine("Most Valuable Lessons");
        output.WriteLine();
        if (r.MostValuableLessons.Count == 0)
        {
            output.WriteLine("  (no retrievals recorded yet)");
        }
        else
        {
            var rank = 1;
            foreach (var lesson in r.MostValuableLessons)
            {
                output.WriteLine($"  {rank}. {Truncate(lesson.RuleText, 80)}");
                output.WriteLine($"     Score: {lesson.Score:0.0}  (retrieved {lesson.RetrievalCount}x × confidence {lesson.Confidence:0.00})");
                rank++;
            }
        }

        output.WriteLine();
        output.WriteLine("Knowledge Growth");
        output.WriteLine();
        if (r.KnowledgeGrowth.Count == 0)
        {
            output.WriteLine("  (no rules yet)");
        }
        else
        {
            foreach (var point in r.KnowledgeGrowth)
            {
                output.WriteLine($"  {point.Period}: {point.CumulativeRules} rules");
            }
        }

        output.WriteLine();
        output.WriteLine("Potentially Stale Rules");
        output.WriteLine();
        if (r.StaleRules.Count == 0)
        {
            output.WriteLine("  (none)");
        }
        else
        {
            var rank = 1;
            foreach (var rule in r.StaleRules)
            {
                var lastRetrieved = rule.DaysSinceLastRetrieved is { } days ? $"{days} days ago" : "never retrieved";
                output.WriteLine($"  {rank}. {Truncate(rule.RuleText, 80)}");
                output.WriteLine($"     Last Retrieved: {lastRetrieved}  |  Confidence: {rule.Confidence:0.00}");
                rank++;
            }
        }

        output.WriteLine();
        output.WriteLine($"Top Conflicting Rules ({r.TotalConflicts} conflict(s) total)");
        output.WriteLine();
        if (r.TopConflictingRules.Count == 0)
        {
            output.WriteLine("  (none)");
        }
        else
        {
            var rank = 1;
            foreach (var rule in r.TopConflictingRules)
            {
                output.WriteLine($"  {rank}. #{rule.RuleId} {Truncate(rule.RuleText, 80)}");
                output.WriteLine($"     In {rule.ConflictCount} conflict(s)");
                rank++;
            }
        }

        WriteOutcomeRuleSection(output, "Most Effective Rules", r.MostEffectiveRules);
        WriteOutcomeRuleSection(output, "Rules With Repeated Negative Outcomes", r.RulesWithRepeatedNegativeOutcomes);

        output.WriteLine();
        output.WriteLine("Frequently Retrieved But Rarely Validated");
        output.WriteLine();
        if (r.FrequentlyRetrievedButRarelyValidated.Count == 0)
        {
            output.WriteLine("  (none)");
        }
        else
        {
            var rank = 1;
            foreach (var rule in r.FrequentlyRetrievedButRarelyValidated)
            {
                output.WriteLine($"  {rank}. #{rule.RuleId} {Truncate(rule.RuleText, 80)}");
                output.WriteLine($"     Retrieved {rule.RetrievalCount}x, no outcomes recorded");
                rank++;
            }
        }

        output.WriteLine();
        output.WriteLine("Mined Lesson Candidates");
        output.WriteLine();
        output.WriteLine($"  Suggested: {r.LessonCandidatesSuggested}  |  Accepted: {r.LessonCandidatesAccepted}  |  Rejected: {r.LessonCandidatesRejected}");
        if (r.TopMinedCategories.Count > 0)
        {
            output.WriteLine("  Top mined categories:");
            foreach (var category in r.TopMinedCategories)
            {
                output.WriteLine($"    {category.Category} ({category.Count})");
            }
        }
    }

    private static void WriteOutcomeRuleSection(TextWriter output, string title, IReadOnlyList<OutcomeRuleStat> rules)
    {
        output.WriteLine();
        output.WriteLine(title);
        output.WriteLine();
        if (rules.Count == 0)
        {
            output.WriteLine("  (none)");
            return;
        }

        var rank = 1;
        foreach (var rule in rules)
        {
            output.WriteLine($"  {rank}. #{rule.RuleId} {Truncate(rule.RuleText, 80)}");
            output.WriteLine($"     Net {rule.NetConfidenceChange:+0.00;-0.00;0.00} over {rule.OutcomeCount} outcome(s)");
            rank++;
        }
    }

    private static void WriteDnaReport(TextWriter output, ProjectDnaReport r)
    {
        output.WriteLine("Project DNA");
        output.WriteLine();
        output.WriteLine("Top Conventions");
        output.WriteLine();
        if (r.TopConventions.Count == 0)
        {
            output.WriteLine("  (no active rules yet)");
        }
        else
        {
            foreach (var convention in r.TopConventions)
            {
                output.WriteLine($"  {convention.Rank}. {Truncate(convention.RuleText, 90)}");
            }
        }

        if (r.CoreCategories.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Core Categories");
            output.WriteLine();
            foreach (var category in r.CoreCategories)
            {
                output.WriteLine($"  {category.Category} ({category.Count})");
            }
        }
    }

    // Project DNA emits snake_case JSON (generated_at, rule_ids, source_counts, …)
    // with enums as names, so the structured output is stable and self-describing.
    private static readonly System.Text.Json.JsonSerializerOptions DnaJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static async Task<int> DnaAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args);
        var json = options.ContainsKey("json");
        var markdown = options.ContainsKey("markdown");

        if (json && markdown)
        {
            output.WriteLine("Choose one of --json or --markdown, not both.");
            return 1;
        }

        var top = 5;
        if (options.TryGetValue("top", out var rawTop))
        {
            if (!int.TryParse(rawTop, out top) || top <= 0)
            {
                output.WriteLine($"Invalid --top '{rawTop}'. Expected a positive integer.");
                return 1;
            }
        }

        ScopeLevel? scopeLevel = null;
        if (options.TryGetValue("scope-level", out var rawLevel))
        {
            if (!Enum.TryParse<ScopeLevel>(rawLevel, ignoreCase: true, out var parsed))
            {
                output.WriteLine($"Invalid --scope-level '{rawLevel}'. Expected Global|Language|Repository|Directory|File.");
                return 1;
            }

            scopeLevel = parsed;
        }

        options.TryGetValue("scope-value", out var scopeValue);
        if (scopeValue is not null && scopeLevel is null)
        {
            output.WriteLine("--scope-value requires --scope-level.");
            return 1;
        }

        var dnaOptions = new Dna.ProjectDnaOptions
        {
            AsOf = DateTimeOffset.UtcNow,
            Top = top,
            ScopeLevel = scopeLevel,
            ScopeValue = scopeValue,
        };

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var dna = scope.ServiceProvider.GetRequiredService<Dna.IProjectDnaService>();
        var report = await dna.GenerateAsync(dnaOptions, cancellationToken).ConfigureAwait(false);

        // Render to a string first so the same content can go to stdout or a file.
        var rendered = new StringWriter();
        if (json)
        {
            rendered.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, DnaJsonOptions));
        }
        else if (markdown)
        {
            WriteDnaMarkdown(rendered, report);
        }
        else
        {
            WriteProjectDna(rendered, report);
        }

        if (options.TryGetValue("output", out var path) && !string.IsNullOrWhiteSpace(path))
        {
            await File.WriteAllTextAsync(path, rendered.ToString(), cancellationToken).ConfigureAwait(false);
            output.WriteLine($"Wrote Project DNA to {path}.");
            return 0;
        }

        output.Write(rendered.ToString());
        return 0;
    }

    private static void WriteProjectDna(TextWriter output, Dna.ProjectDnaReport report)
    {
        output.WriteLine("Project DNA");
        output.WriteLine();
        output.WriteLine($"Generated: {report.GeneratedAt:u}");
        output.WriteLine($"Scope:     {DescribeScope(report.Scope)}");
        var counts = report.SourceCounts;
        output.WriteLine($"Sources:   {counts.ActiveRules} active, {counts.PromotedRules} promoted, {counts.PendingRules} pending; {counts.LessonCandidates} mined; {counts.Conflicts} conflict(s)");

        if (counts.ActiveRules + counts.PromotedRules + counts.PendingRules + counts.LessonCandidates == 0)
        {
            output.WriteLine();
            output.WriteLine("No rules captured yet. Record feedback with `agentrecall feedback add`");
            output.WriteLine("to start building this project's DNA.");
            return;
        }

        foreach (var section in report.Sections)
        {
            output.WriteLine();
            output.WriteLine(section.Title);
            output.WriteLine();
            if (section.Items.Count == 0)
            {
                output.WriteLine("  (none yet)");
                continue;
            }

            foreach (var item in section.Items)
            {
                output.WriteLine($"  - {Truncate(item.Text, 100)}");
            }
        }
    }

    private static void WriteDnaMarkdown(TextWriter output, Dna.ProjectDnaReport report)
    {
        output.WriteLine("# Project DNA");
        output.WriteLine();
        output.WriteLine($"_Generated {report.GeneratedAt:u} · scope: {DescribeScope(report.Scope)}_");

        var counts = report.SourceCounts;
        if (counts.ActiveRules + counts.PromotedRules + counts.PendingRules + counts.LessonCandidates == 0)
        {
            output.WriteLine();
            output.WriteLine("_No rules captured yet. Record feedback with `agentrecall feedback add` to start building this project's DNA._");
            return;
        }

        foreach (var section in report.Sections)
        {
            output.WriteLine();
            output.WriteLine($"## {section.Title}");
            output.WriteLine();
            if (section.Items.Count == 0)
            {
                output.WriteLine("_No entries yet._");
                continue;
            }

            foreach (var item in section.Items)
            {
                output.WriteLine($"- {item.Text}");
            }
        }
    }

    private static string DescribeScope(Dna.DnaScope scope) =>
        scope.Level is null
            ? "all"
            : string.IsNullOrEmpty(scope.Value) ? scope.Level.ToString()! : $"{scope.Level}:{scope.Value}";

    /// <summary>Ensures the database exists before a command touches it.</summary>
    private static Task EnsureInitializedAsync(AsyncServiceScope scope, CancellationToken cancellationToken) =>
        scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(cancellationToken);

    /// <summary>
    /// Parses <c>--key value</c> pairs into a dictionary. A flag with no
    /// following value (or followed by another <c>--flag</c>) maps to "true".
    /// </summary>
    private static Dictionary<string, string> ParseOptions(string[] tokens)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = token[2..];
            if (i + 1 < tokens.Length && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[key] = tokens[++i];
            }
            else
            {
                options[key] = "true";
            }
        }

        return options;
    }

    private static void WriteRule(TextWriter output, RecallRule rule)
    {
        output.WriteLine($"Rule #{rule.Id} (v{rule.Version}) [{rule.Status}]");
        output.WriteLine($"  Category:          {rule.Category}");
        output.WriteLine($"  Trigger:           {rule.Trigger}");
        output.WriteLine($"  Mistake:           {rule.Mistake}");
        output.WriteLine($"  Rule:              {rule.RuleText}");
        output.WriteLine($"  Technical context: {rule.TechnicalContext}");
        output.WriteLine($"  Tags:              {rule.Tags}");
        output.WriteLine($"  Confidence:        {rule.Confidence:0.00}");
        output.WriteLine($"  Scope:             {rule.ScopeLevel}:{rule.ScopeValue}");
        output.WriteLine($"  Superseded by:     {(rule.SupersededById?.ToString() ?? "-")}");
        output.WriteLine($"  Created:           {rule.CreatedAt:u}");
        output.WriteLine($"  Updated:           {rule.UpdatedAt:u}");
    }

    private static void WriteRuleExplanation(
        TextWriter output,
        RecallRule rule,
        IReadOnlyList<RecallEvent> events,
        IReadOnlyList<RuleOutcome> outcomes)
    {
        var ruleOutcomes = outcomes.Where(o => o.RuleId == rule.Id).ToList();
        var retrievedCount = events.Count(e => e.Type == RecallEventType.RuleApplied && e.RuleId == rule.Id);
        var netChange = Math.Round(ruleOutcomes.Sum(o => o.ConfidenceDelta), 2);

        output.WriteLine("Rule:");
        output.WriteLine(rule.RuleText);
        output.WriteLine();
        output.WriteLine("Confidence:");
        output.WriteLine($"{rule.Confidence:0.00}");
        output.WriteLine();
        output.WriteLine("Why this confidence:");

        var origin = rule.Status is RuleStatus.Active or RuleStatus.Promoted
            ? "accepted feedback"
            : "pending feedback";
        output.WriteLine($"- Created from {origin} as a {rule.Category} rule");
        output.WriteLine($"- Retrieved {retrievedCount} time(s)");

        foreach (var group in ruleOutcomes
            .GroupBy(o => o.Type)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.ToString(), StringComparer.Ordinal))
        {
            output.WriteLine($"- {group.Key}: {group.Count()} time(s)");
        }

        if (ruleOutcomes.Count == 0)
        {
            output.WriteLine("- No outcomes recorded yet");
        }

        output.WriteLine();
        output.WriteLine("Net confidence change:");
        output.WriteLine($"{netChange:+0.00;-0.00;0.00}");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine($"{AppInfo.Name} - local-first memory and learning for AI coding agents");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine($"  {AppInfo.Name} <command> [options]");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  init                 Create the local data directory and database");
        output.WriteLine("  setup                Ensure the .NET tools directory is on your PATH");
        output.WriteLine("  devcontainer init    Scaffold dev container wiring so AgentRecall");
        output.WriteLine("                       reinstalls automatically on every rebuild");
        output.WriteLine("  feedback add         Record feedback and extract a pending rule");
        output.WriteLine("  rules list           List all rules");
        output.WriteLine("  rules show <id>      Show a single rule in detail");
        output.WriteLine("  rules approve <id>   Move a Pending rule to Active");
        output.WriteLine("  rules promote <id>   Promote a rule");
        output.WriteLine("  rules supersede <oldId> <newId>");
        output.WriteLine("                       Replace one rule with another");
        output.WriteLine("  rules archive <id>   Archive a rule (excluded from search)");
        output.WriteLine("  rules conflicts      List detected rule conflicts and the chosen winner (--json)");
        output.WriteLine("  rules explain <id>   Explain a rule's confidence from its outcome history");
        output.WriteLine("  outcome record       Record an outcome (TestsPassed, UserAccepted, …) and adjust confidence");
        output.WriteLine("  lessons mine         Mine repeated historical signals into suggested lesson candidates");
        output.WriteLine("  lessons list|show|accept|reject");
        output.WriteLine("                       Review mined candidates; accept turns one into a rule");
        output.WriteLine("  lifecycle suggest    Recommend lifecycle actions (promote/archive/supersede/review)");
        output.WriteLine("  lifecycle list|show|apply|reject");
        output.WriteLine("                       Review recommendations; suggest is dry-run unless --apply");
        output.WriteLine("  search \"<query>\"     Search rules by keyword, ranked");
        output.WriteLine("  inject-context \"<task>\"");
        output.WriteLine("                       Build agent-ready context (must-follow, warnings,");
        output.WriteLine("                       preferred/anti-patterns) for a task");
        output.WriteLine("  import build-log <file>");
        output.WriteLine("  import test-log <file>");
        output.WriteLine("  import lint-log <file>");
        output.WriteLine("                       Ingest a failure log into events");
        output.WriteLine("  import pr-comments <file>");
        output.WriteLine("                       Capture PR review comments as pending rules");
        output.WriteLine("  eval retrieval       Evaluate retrieval quality against the bundled dataset");
        output.WriteLine("  report monthly       Monthly learning report (captures, promotions, usage)");
        output.WriteLine("  report lifecycle     Cradle-to-grave rule lifecycle counts");
        output.WriteLine("  report usage         Top retrieved, most valuable, growth, and stale rules");
        output.WriteLine("  report dna           Distil the project's conventions for onboarding");
        output.WriteLine("                       (any report supports --json)");
        output.WriteLine("  dna                  Summarise the repo's engineering personality for");
        output.WriteLine("                       onboarding (--markdown, --json, --top <n>,");
        output.WriteLine("                       --scope-level <level>, --scope-value <text>,");
        output.WriteLine("                       --output <file>)");
        output.WriteLine("  hook user-prompt-submit");
        output.WriteLine("                       Gated context injection for a Claude Code UserPromptSubmit hook");
        output.WriteLine("  mcp                  Run the MCP server over stdio (for Claude Code)");
        output.WriteLine("  status               Show the memory subsystem status");
        output.WriteLine("  help                 Show this help text");
        output.WriteLine("  version              Show the installed version");
        output.WriteLine();
        output.WriteLine("feedback add options:");
        output.WriteLine("  --task <text>          (required) what the agent was asked to do");
        output.WriteLine("  --feedback <text>      (required) the corrective guidance");
        output.WriteLine("  --bad-output <text>    the undesirable output");
        output.WriteLine("  --fixed-output <text>  the corrected/preferred output");
        output.WriteLine("  --scope-level <level>  Global|Language|Repository|Directory|File");
        output.WriteLine("  --scope-value <text>   scope identifier (repo, language, path)");
        output.WriteLine("  --tags <a,b,c>         comma-separated tags");
        output.WriteLine("  --pending              keep the rule Pending instead of approving it");
    }
}
