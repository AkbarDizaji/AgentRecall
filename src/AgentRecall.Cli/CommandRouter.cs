using AgentRecall.Cli.Commands;
using AgentRecall.Core;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Evaluation;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Finalization;
using AgentRecall.Core.Reporting;
using AgentRecall.Core.Search;
using AgentRecall.Core.Services;
using AgentRecall.Core.Summary;
using AgentRecall.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Dna = AgentRecall.Core.Dna;

namespace AgentRecall.Cli;

/// <summary>
/// Parses command-line arguments and dispatches to the matching command.
/// Returns the process exit code.
/// </summary>
public static partial class CommandRouter
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

        // Thin dispatch: the first argument selects a command handler from the table.
        if (Dispatch.TryGetValue(command, out var handler))
        {
            return await handler.ExecuteAsync(rest, services, output, logger, cancellationToken).ConfigureAwait(false);
        }

        output.WriteLine($"Unknown command: {command}");
        output.WriteLine();
        WriteHelp(output);
        return 1;
    }

    /// <summary>
    /// The command table: maps each command name (and its aliases) to an
    /// <see cref="ICommand"/>. Built once; the handlers are static and capture no state.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, ICommand> Dispatch = BuildDispatch();

    private static Dictionary<string, ICommand> BuildDispatch()
    {
        var version = new DelegateCommand((_, _, o, _, _) =>
        {
            o.WriteLine($"{AppInfo.Name} {AppInfo.Version}");
            return Task.FromResult(0);
        });
        var help = new DelegateCommand((_, _, o, _, _) =>
        {
            WriteHelp(o);
            return Task.FromResult(0);
        });

        return new Dictionary<string, ICommand>(StringComparer.Ordinal)
        {
            ["--version"] = version,
            ["-v"] = version,
            ["version"] = version,
            ["help"] = help,
            ["--help"] = help,
            ["-h"] = help,
            ["init"] = new DelegateCommand((_, s, o, l, ct) => InitAsync(s, o, l, ct)),
            ["devcontainer"] = new DelegateCommand((a, _, o, _, _) => Task.FromResult(DevcontainerInit(a, o))),
            ["claude-code"] = new DelegateCommand((a, _, o, _, _) => Task.FromResult(ClaudeCodeInit(a, o))),
            ["setup"] = new DelegateCommand((_, _, o, _, _) => Task.FromResult(SetupPath(o))),
            ["feedback"] = new DelegateCommand((a, s, o, l, ct) => FeedbackAsync(a, s, o, l, ct)),
            ["rules"] = new DelegateCommand((a, s, o, l, ct) => RulesAsync(a, s, o, l, ct)),
            ["search"] = new DelegateCommand((a, s, o, l, ct) => SearchAsync(a, s, o, l, ct)),
            ["inject-context"] = new DelegateCommand((a, s, o, _, ct) => InjectContextAsync(a, s, o, ct)),
            ["import"] = new DelegateCommand((a, s, o, l, ct) => ImportAsync(a, s, o, l, ct)),
            ["eval"] = new DelegateCommand((a, _, o, _, ct) => EvalAsync(a, o, ct)),
            ["report"] = new DelegateCommand((a, s, o, _, ct) => ReportAsync(a, s, o, ct)),
            ["dna"] = new DelegateCommand((a, s, o, _, ct) => DnaAsync(a, s, o, ct)),
            ["outcome"] = new DelegateCommand((a, s, o, _, ct) => OutcomeAsync(a, s, o, ct)),
            ["lessons"] = new DelegateCommand((a, s, o, _, ct) => LessonsAsync(a, s, o, ct)),
            ["lifecycle"] = new DelegateCommand((a, s, o, _, ct) => LifecycleAsync(a, s, o, ct)),
            ["hook"] = new DelegateCommand((a, s, o, _, ct) => HookAsync(a, s, o, ct)),
            ["finalize-turn"] = new DelegateCommand((a, s, o, _, ct) => FinalizeTurnAsync("finalize-turn", a, s, o, ct)),
            ["capture-status"] = new DelegateCommand((a, s, o, _, ct) => FinalizeTurnAsync("capture-status", a, s, o, ct)),
            ["turn-summary"] = new DelegateCommand((a, s, o, _, ct) => TurnSummaryAsync(a, s, o, ct)),
            ["activity"] = new DelegateCommand((a, s, o, _, ct) => ActivityAsync(a, s, o, ct)),
            ["seed"] = new DelegateCommand((a, s, o, l, ct) => SeedAsync(a, s, o, l, ct)),
            ["career"] = new DelegateCommand((a, s, o, l, ct) => CareerAsync(a, s, o, l, ct)),
            ["document"] = new DelegateCommand((a, s, o, l, ct) => DocumentAsync(a, s, o, l, ct)),
            ["cleanup"] = new DelegateCommand((a, s, o, l, ct) => CleanupAsync(a, s, o, l, ct)),
            ["doctor"] = new DelegateCommand((a, s, o, l, ct) => DoctorAsync(a, s, o, l, ct)),
            ["mcp"] = new DelegateCommand(async (_, s, o, _, ct) =>
            {
                var server = new Mcp.McpServer(s);
                await server.RunAsync(Console.In, o, ct).ConfigureAwait(false);
                return 0;
            }),
            ["status"] = new DelegateCommand((_, s, o, _, _) =>
            {
                o.WriteLine(s.GetRequiredService<IMemoryService>().Status());
                return Task.FromResult(0);
            }),
        };
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
            output.WriteLine("Using Claude Code? Run `agentrecall claude-code init` here to enable automatic recall + capture.");
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
            output.WriteLine("Usage: agentrecall devcontainer init [path] [--create]");
            output.WriteLine("(wires AgentRecall's recall/capture hooks and CLAUDE.md guidance;");
            output.WriteLine(" --create also scaffolds a dev container that reinstalls it on every rebuild)");
            output.WriteLine("Not using a dev container? `agentrecall claude-code init` does the same");
            output.WriteLine("hook/CLAUDE.md wiring with no container scaffolding.");
            return 1;
        }

        // Optional positional path; defaults to the current directory. --create opts into
        // scaffolding a dev container when the project has none.
        var projectRoot = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? Directory.GetCurrentDirectory();
        var createDevcontainer = args.Any(a => string.Equals(a, "--create", StringComparison.Ordinal));

        return RunScaffold(projectRoot, createDevcontainer, output);
    }

    private static int ClaudeCodeInit(string[] args, TextWriter output)
    {
        var sub = args.Length > 0 ? args[0] : string.Empty;
        if (sub != "init")
        {
            output.WriteLine("Usage: agentrecall claude-code init [path]");
            output.WriteLine("(wires AgentRecall's recall/capture hooks and CLAUDE.md guidance for Claude Code —");
            output.WriteLine(" no dev container scaffolding; use `agentrecall devcontainer init --create` for that)");
            return 1;
        }

        var projectRoot = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? Directory.GetCurrentDirectory();

        return RunScaffold(projectRoot, createDevcontainer: false, output);
    }

    private static int RunScaffold(string projectRoot, bool createDevcontainer, TextWriter output)
    {
        try
        {
            var result = Devcontainer.DevcontainerScaffolder.Init(projectRoot, createDevcontainer);

            if (result.WroteScript)
            {
                var verb = result.ScriptOverwritten ? "Updated" : "Wrote";
                output.WriteLine($"{verb} {result.ScriptPath} (installs AgentRecall from NuGet on container create/rebuild).");
            }

            WriteHookOutcome(output, result.HookOutcome, result.ClaudeSettingsPath,
                "UserPromptSubmit", "automatic rule injection", Devcontainer.DevcontainerScaffolder.HookCommand);
            WriteHookOutcome(output, result.PreToolUseHookOutcome, result.ClaudeSettingsPath,
                "PreToolUse", "per-write rule injection", Devcontainer.DevcontainerScaffolder.PreToolUseHookCommand);
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
                case Devcontainer.GuidanceOutcome.Updated:
                    output.WriteLine($"Updated the AgentRecall guidance block in {result.ClaudeMdPath} in place (refreshed the behavior contract; no duplicate added).");
                    break;
                case Devcontainer.GuidanceOutcome.AlreadyPresent:
                    output.WriteLine($"AgentRecall guidance already current in {result.ClaudeMdPath}; left it as is.");
                    break;
            }

            if (result.DevcontainerDeferred)
            {
                // No dev container present and none requested: don't impose one.
                output.WriteLine();
                output.WriteLine(result.ManualSteps);
            }
            else if (result.CreatedDevcontainerJson)
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

            var noticeLevel = services.GetRequiredService<AgentRecallOptions>().ResolvedActivityNoticeLevel;
            var notice = ActivityNoticeFactory.ForFeedback(result, "cli");
            if (notice is not null)
            {
                await scope.ServiceProvider.GetRequiredService<IActivityRecorder>()
                    .RecordAsync(notice, cancellationToken).ConfigureAwait(false);
            }

            if (result.Rule is null)
            {
                // Skipped by the capture decision: a low-value code fact, or a duplicate.
                var reason = result.Decision?.Reason ?? result.Worthiness?.Reason ?? "not memory-worthy";
                output.WriteLine($"Not stored ({result.Decision?.Outcome.ToString() ?? "Skip"}): {reason}");
                output.WriteLine("Store a reusable lesson instead, or pass --feedback with the broader rule.");
                PrintNotice(output, notice, noticeLevel);
                return 0;
            }

            if (result.Event is not null)
            {
                output.WriteLine($"Recorded feedback as event #{result.Event.Id}.");
            }

            // Interactive Memory: surface an ambiguous SuggestCapture as a y/n/v prompt when
            // a terminal is attached, or a non-blocking "approve later" notice otherwise. It
            // owns the user-facing output for suggestions and remembered/ignored outcomes; an
            // auto-capture falls through to the standard confirmation below.
            var mode = services.GetRequiredService<AgentRecallOptions>().ResolvedInteractiveMemoryMode;
            var isInteractive = !Console.IsInputRedirected;
            var interaction = await InteractiveMemory
                .HandleAsync(result, mode, isInteractive, Console.In, output, scope.ServiceProvider, cancellationToken)
                .ConfigureAwait(false);

            if (interaction is InteractiveMemoryOutcome.AutoCaptured or InteractiveMemoryOutcome.ReusedDuplicate)
            {
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

                PrintNotice(output, notice, noticeLevel);
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
                var listOptions = ParseOptions(args[1..]);
                RuleStatus? statusFilter = null;
                if (listOptions.TryGetValue("status", out var rawStatus))
                {
                    if (!Enum.TryParse(rawStatus, ignoreCase: true, out RuleStatus parsed))
                    {
                        output.WriteLine($"Invalid --status '{rawStatus}'. Valid values: {string.Join(", ", Enum.GetNames<RuleStatus>())}");
                        return 1;
                    }

                    statusFilter = parsed;
                }

                var all = await rules.ListAsync(cancellationToken).ConfigureAwait(false);
                if (statusFilter is { } status)
                {
                    all = all.Where(r => r.Status == status).ToList();
                }

                if (all.Count == 0)
                {
                    output.WriteLine(statusFilter is { } s
                        ? $"No {s} rules."
                        : "No rules yet. Add one with: agentrecall feedback add ...");
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
                // Bulk form: resolve every rule still awaiting the capture-approval gate's
                // yes/no, scoped to one chat (the CLI-terminal equivalent of "yes to all").
                if (sub != "promote" && args.Length > 1 && string.Equals(args[1], "--all-pending", StringComparison.Ordinal))
                {
                    var bulkOptions = ParseOptions(args[2..]);
                    bulkOptions.TryGetValue("session", out var sessionId);

                    var approvals = scope.ServiceProvider.GetRequiredService<IPendingCaptureApprovalService>();
                    var batch = sub == "approve"
                        ? await approvals.ApproveAllAsync(sessionId, cancellationToken).ConfigureAwait(false)
                        : await approvals.RejectAllAsync(sessionId, cancellationToken).ConfigureAwait(false);

                    if (batch.RuleIds.Count == 0)
                    {
                        output.WriteLine("Nothing is awaiting approval.");
                        return 0;
                    }

                    var verb = sub == "approve" ? "approved" : "archived";
                    var ids = string.Join(", ", batch.RuleIds.Select(id => $"#{id}"));
                    output.WriteLine($"{batch.RuleIds.Count} rule(s) {verb}: {ids}.");
                    return 0;
                }

                if (args.Length < 2 || !int.TryParse(args[1], out var id))
                {
                    var allPendingHint = sub == "promote" ? "" : $" | agentrecall rules {sub} --all-pending [--session <id>]";
                    output.WriteLine($"Usage: agentrecall rules {sub} <id>{allPendingHint}");
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

            case "report-bad":
            {
                if (args.Length < 2 || !int.TryParse(args[1], out var id))
                {
                    output.WriteLine("Usage: agentrecall rules report-bad <id> [--reason <text>]");
                    return 1;
                }

                var reportOptions = ParseOptions(args[2..]);
                reportOptions.TryGetValue("reason", out var reason);

                var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();
                try
                {
                    var archived = await lifecycle.ReportBadAsync(id, reason, cancellationToken).ConfigureAwait(false);
                    output.WriteLine($"Rule #{archived.Id} reported as bad and is now {archived.Status}.");
                    return 0;
                }
                catch (KeyNotFoundException ex)
                {
                    output.WriteLine(ex.Message);
                    return 1;
                }
            }

            case "delete":
            {
                if (args.Length < 2 || !int.TryParse(args[1], out var id))
                {
                    output.WriteLine("Usage: agentrecall rules delete <id> [--force]");
                    return 1;
                }

                var force = ParseOptions(args[2..]).ContainsKey("force");

                var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();
                try
                {
                    var deleted = await lifecycle.DeleteAsync(id, force, cancellationToken).ConfigureAwait(false);
                    output.WriteLine($"Rule #{deleted.Id} permanently deleted (was {deleted.Status}).");
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
                output.WriteLine("  agentrecall rules list [--status <status>]");
                output.WriteLine("  agentrecall rules show <id>");
                output.WriteLine("  agentrecall rules approve <id>");
                output.WriteLine("  agentrecall rules promote <id>");
                output.WriteLine("  agentrecall rules supersede <oldId> <newId>");
                output.WriteLine("  agentrecall rules archive <id>");
                output.WriteLine("  agentrecall rules report-bad <id> [--reason <text>]");
                output.WriteLine("  agentrecall rules delete <id> [--force]");
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

                var lifecycleNotice = ActivityNoticeFactory.ForLifecycle(recs, "cli");
                if (lifecycleNotice is not null)
                {
                    await scope.ServiceProvider.GetRequiredService<IActivityRecorder>()
                        .RecordAsync(lifecycleNotice, cancellationToken).ConfigureAwait(false);
                }

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

                PrintNotice(output, lifecycleNotice, services.GetRequiredService<AgentRecallOptions>().ResolvedActivityNoticeLevel);
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

                var mineNotice = ActivityNoticeFactory.ForLessonsMined(result, "cli");
                if (mineNotice is not null)
                {
                    await scope.ServiceProvider.GetRequiredService<IActivityRecorder>()
                        .RecordAsync(mineNotice, cancellationToken).ConfigureAwait(false);
                }

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
                PrintNotice(output, mineNotice, services.GetRequiredService<AgentRecallOptions>().ResolvedActivityNoticeLevel);
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
            output.WriteLine("Usage: agentrecall inject-context \"<task>\" [--scope-level <level>] [--scope-value <text>] [--file-path <path>] [--limit <n>] [--include-pending] [--no-notice]");
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

        // Record the activity for the human-visible log. The notice is suppressed for
        // machine/context use (--no-notice) so inject-context output can be fed to a
        // model without the verbose human notice inflating its tokens.
        var recorder = scope.ServiceProvider.GetRequiredService<IActivityRecorder>();
        var fetched = ActivityNoticeFactory.ForContextFetched(result, "cli");
        var conflictNotice = ActivityNoticeFactory.ForConflictResolved(result.Conflicts, "cli");
        if (fetched is not null) await recorder.RecordAsync(fetched, cancellationToken).ConfigureAwait(false);
        if (conflictNotice is not null) await recorder.RecordAsync(conflictNotice, cancellationToken).ConfigureAwait(false);

        if (!options.ContainsKey("no-notice"))
        {
            var level = services.GetRequiredService<AgentRecallOptions>().ResolvedActivityNoticeLevel;
            PrintNotice(output, fetched, level);
            PrintNotice(output, conflictNotice, level);
        }

        return 0;
    }

    /// <summary>
    /// Writes a rendered human notice, with a blank line before it, when the notice is
    /// present and the level is not Silent. A no-op otherwise.
    /// </summary>
    private static void PrintNotice(TextWriter output, ActivityNotice? notice, NoticeLevel level)
    {
        if (notice is null)
        {
            return;
        }

        var rendered = ActivityNoticeRenderer.Render(notice, level);
        if (!string.IsNullOrEmpty(rendered))
        {
            output.WriteLine();
            output.WriteLine(rendered);
        }
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

            case "pre-tool-use":
            {
                var payload = await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var injection = await Hooks.PreToolUseHook
                    .RunAsync(payload, services, Console.Error, cancellationToken)
                    .ConfigureAwait(false);

                // The model reads only hookSpecificOutput.additionalContext, so the rule text goes
                // there; the "fetched N rules" status line goes to systemMessage (user-facing).
                // Emit nothing when there was no relevant rule, so a write is never annotated needlessly.
                if (!injection.IsEmpty)
                {
                    var response = new System.Text.Json.Nodes.JsonObject
                    {
                        ["hookSpecificOutput"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["hookEventName"] = "PreToolUse",
                            ["additionalContext"] = injection.AdditionalContext,
                        },
                    };

                    if (!string.IsNullOrEmpty(injection.SystemMessage))
                    {
                        response["systemMessage"] = injection.SystemMessage;
                    }

                    output.WriteLine(response.ToJsonString());
                }

                // Always succeed so the hook never blocks the write.
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
                output.WriteLine("Usage: agentrecall hook <user-prompt-submit|pre-tool-use|capture>");
                output.WriteLine("(reads the Claude Code hook payload on stdin; user-prompt-submit injects");
                output.WriteLine(" recall context at turn start, pre-tool-use injects rules relevant to the file");
                output.WriteLine(" about to be written, capture stores reusable lessons after a turn)");
                return 1;
        }
    }

    /// <summary>
    /// The canonical capture path for a completed turn. With a payload on stdin it
    /// finalizes the turn (extract, classify, dedup, decide, store) and prints a
    /// structured summary; with <c>status</c> / <c>--last</c> it reports the last
    /// finalization so the agent can give a definitive answer instead of guessing.
    /// Always exits 0 so it never blocks Claude Code.
    /// </summary>
    private static async Task<int> FinalizeTurnAsync(
        string command,
        string[] args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args);
        var json = options.ContainsKey("json");
        var hook = options.ContainsKey("hook");
        var isStatus =
            command == "capture-status" ||
            options.ContainsKey("last") ||
            options.ContainsKey("last-turn") ||
            args.Any(a => string.Equals(a, "status", StringComparison.Ordinal));

        try
        {
            await using var scope = services.CreateAsyncScope();
            await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
            var finalizer = scope.ServiceProvider.GetRequiredService<ITurnFinalizer>();

            if (isStatus)
            {
                var last = await finalizer.GetLastAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                // A blocked turn has no finalization of its own yet. Report that it is waiting on a
                // judgment rather than answering with the previous turn's decision.
                var awaiting = await Mcp.Tools.JudgmentStatus
                    .FindAwaitingAsync(scope.ServiceProvider, sessionId: null, cwd: null, cancellationToken)
                    .ConfigureAwait(false);

                if (json)
                {
                    WriteJson(output, FinalizationJson(last, awaiting?.Id));
                }
                else if (awaiting is not null)
                {
                    output.WriteLine(TurnFinalizationFormatter.AwaitingJudgmentLine(awaiting.Id));
                }
                else if (last is null)
                {
                    output.WriteLine("No finalization recorded yet.");
                }
                else
                {
                    RenderFinalization(output, last);
                    if (command == "capture-status")
                    {
                        // capture-status answers "what did capture decide?"; point to the
                        // turn summary for the full per-turn activity without duplicating it.
                        output.WriteLine();
                        output.WriteLine("For full turn activity: agentrecall turn-summary --last");
                    }
                }

                return 0;
            }

            var payload = await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var input = Hooks.TurnPayload.Parse(payload, Console.Error);

            if (input is null)
            {
                // Malformed or empty payload: no DB mutation, structured empty result.
                if (json)
                {
                    WriteJson(output, FinalizationJson(null));
                }
                else if (!hook)
                {
                    output.WriteLine("No lessons found.");
                }

                return 0;
            }

            // Enforced judgment, on the Stop-hook surface only. A turn nobody judged is blocked once
            // and the session model — the judge — is asked to submit its verdict; the turn resumes,
            // calls submit_capture_judgment, and the next Stop finalizes from the recorded verdict.
            // The CLI/--json surfaces never block, so scripted and manual finalization behave as
            // before. Nothing here decides what to remember; it only decides whether to wait for
            // the decision.
            if (hook)
            {
                var gateDecision = await EvaluateJudgmentGateAsync(scope.ServiceProvider, input, cancellationToken)
                    .ConfigureAwait(false);

                if (gateDecision.Action == Core.Finalization.JudgmentEnforcementAction.RequestJudgment)
                {
                    // Ask for the outcomes in the same breath as the judgment: the turn already
                    // stops once here, and a second seam would be a second interruption.
                    var awaitingOutcome = await RulesAwaitingOutcomeAsync(
                        scope.ServiceProvider, input, cancellationToken).ConfigureAwait(false);

                    EmitJudgmentBlock(output, gateDecision, awaitingOutcome);
                    return 0;
                }

                if (gateDecision.Action == Core.Finalization.JudgmentEnforcementAction.ProceedUnjudged)
                {
                    // Record which silence this was, so the finalization says "asked and not
                    // answered" rather than "never judged".
                    input = input with { JudgmentRequestExhausted = true };
                }
            }

            var gate = scope.ServiceProvider.GetRequiredService<Core.Finalization.ITurnJudgmentGate>();
            Core.Finalization.TurnFinalizationResult result;

            // A self-reported verdict (the model piping `finalize-turn` itself) names its turn only
            // loosely: the model retypes the prompt, and a retyped prompt derives a different turn id
            // than the payload that was blocked. So its turn id cannot be trusted and the outstanding
            // ask for the chat is the better identifier — route it through the same seam the tool
            // uses, which answers that ask from the turn text AgentRecall stored. Otherwise the
            // verdict finalizes a phantom turn beside the real one: the ask stays open, the next
            // end-of-turn run records the turn as unjudged, and its summary reports zero captures for
            // a turn that captured a rule.
            //
            // A judgment on a hook payload is the opposite case: Claude Code supplied that turn's
            // text, so its turn id is authoritative and must not be overridden by whatever ask
            // happens to be open — it closes its own ask, and only its own.
            if (input.SuppliedJudgment is not null && !hook)
            {
                var submission = await gate.SubmitAsync(
                    new Core.Finalization.JudgmentSubmission
                    {
                        Verdict = input.SuppliedJudgment,
                        SessionId = input.SessionId,
                        Cwd = input.Cwd,
                        Prompt = input.Prompt,
                        AssistantResponse = input.AssistantResponse,
                        ScopeLevel = input.ScopeLevel,
                        ScopeValue = input.ScopeValue,
                        Source = input.Source ?? "model-self-judged",
                    },
                    cancellationToken).ConfigureAwait(false);

                // A refused submission is not a reason to lose the turn: finalize it directly, which
                // is what this path did before there was anything to submit to.
                result = submission.Finalization
                    ?? await finalizer.FinalizeAsync(input, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await finalizer.FinalizeAsync(input, cancellationToken).ConfigureAwait(false);

                // A judgment can also reach the finalizer without passing through the gate at all
                // (a cached re-finalization of a judged turn). Close the ask it answers so the
                // status surfaces stop reporting the turn as unanswered.
                await gate.CloseOutstandingAsync(input, result, cancellationToken).ConfigureAwait(false);
            }

            // The other half of recall: how the rules this turn injected actually fared. The report
            // is host-supplied like the capture judgment — validated here, never inferred — and the
            // rules nobody reported on are recorded as unreported, so an empty confidence ledger is
            // visible instead of silent.
            var outcomeReport = await ApplyRuleOutcomesAsync(
                scope.ServiceProvider, input, result, cancellationToken).ConfigureAwait(false);

            // Passive seed reinforcement: a seed rule used repeatedly across turns without a
            // correction earns a small, capped confidence bump. Runs on the end-of-turn path
            // so evolution is deterministic and never touches the hot retrieval path.
            var seedReinforcement = await scope.ServiceProvider.GetRequiredService<Core.Seeds.ISeedConfidenceService>()
                .ReinforceAsync(cancellationToken).ConfigureAwait(false);
            var seedNotice = ActivityNoticeFactory.ForSeedReinforced(seedReinforcement, input.Source ?? "cli");
            if (seedNotice is not null)
            {
                await scope.ServiceProvider.GetRequiredService<IActivityRecorder>()
                    .RecordAsync(seedNotice with { TurnId = result.TurnId }, cancellationToken).ConfigureAwait(false);
            }

            // Record the finalization for the human-visible log (deduped by turn id, so
            // a cached re-finalization never double-logs). Stamp the turn id so this
            // capture joins the rules used earlier in the same turn.
            var notice = ActivityNoticeFactory.ForTurnFinalized(result, input.Source ?? "cli");
            if (notice is not null)
            {
                await scope.ServiceProvider.GetRequiredService<IActivityRecorder>()
                    .RecordAsync(notice with { TurnId = result.TurnId }, cancellationToken).ConfigureAwait(false);
            }

            // Structured skip activity for candidates the Stop-hook quality gate (or a
            // do-not-save instruction) rejected, so capture-status and the turn summary
            // explain the skip from actual state — never from a guess. Capped excerpt only.
            foreach (var skip in result.Skipped.Where(s => s.SkipReason != CaptureSkipReason.None))
            {
                var skipNotice = ActivityNoticeFactory.ForCandidateSkipped(
                    skip.SkipReason, skip.CandidateExcerpt, input.Source ?? "cli");
                if (skipNotice is not null)
                {
                    await scope.ServiceProvider.GetRequiredService<IActivityRecorder>()
                        .RecordAsync(skipNotice with { TurnId = result.TurnId }, cancellationToken).ConfigureAwait(false);
                }
            }

            // Optional career-impact: a cheap, deterministic detector that runs only when the
            // `career-impact` pack is installed and the mode is not Silent. It persists a
            // candidate and records a human-visible notice; the full journal is never
            // generated here (only on demand). The compact summary is printed on the CLI path,
            // but the hook (model-visible) path emits only a short pointer via the turn summary.
            var career = await AnalyzeCareerImpactAsync(scope.ServiceProvider, input, result, cancellationToken)
                .ConfigureAwait(false);

            // Optional document-opportunity: a host-supplied judge (same architecture as the
            // semantic capture judge) that offers generating a durable document. It persists a
            // candidate and records a notice; it never writes a file itself — only
            // `agentrecall document write` does, on demand. Compact summary on the CLI path;
            // the hook path emits only a short pointer via the turn summary.
            var docOpportunity = await AnalyzeDocOpportunityAsync(scope.ServiceProvider, input, result, cancellationToken)
                .ConfigureAwait(false);

            if (json)
            {
                WriteJson(output, FinalizationJson(result));
            }
            else if (hook)
            {
                // The Stop-hook surface prints the aggregated Turn Memory Summary (one
                // bounded systemMessage), governed by TurnSummaryLevel. It never blocks.
                await EmitTurnSummaryHookAsync(scope.ServiceProvider, output, result, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                RenderFinalization(output, result);
                RenderRuleOutcomes(output, outcomeReport);
                var level = services.GetRequiredService<AgentRecallOptions>().ResolvedActivityNoticeLevel;
                PrintNotice(output, notice, level);
                PrintNotice(output, seedNotice, level);
                PrintCareerImpact(output, career, services.GetRequiredService<AgentRecallOptions>());
                PrintDocOpportunity(output, docOpportunity);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block Claude Code; surface the error on stderr only.
            Console.Error.WriteLine($"[agentrecall] finalize-turn failed: {ex.Message}");
            if (json)
            {
                WriteJson(output, new
                {
                    captured = Array.Empty<object>(),
                    suggested = Array.Empty<object>(),
                    skipped = Array.Empty<object>(),
                    duplicates = Array.Empty<int>(),
                    errors = new[] { ex.Message },
                });
            }
        }

        return 0;
    }

    /// <summary>
    /// Asks the judgment gate what to do with a turn and records the ask when it decides to make
    /// one. Failures are swallowed into a "finalize" decision: enforcement must never be the reason
    /// a turn cannot end, and a block that could not be recorded is a block that could repeat.
    /// </summary>
    private static async Task<Core.Finalization.JudgmentGateDecision> EvaluateJudgmentGateAsync(
        IServiceProvider services,
        Core.Finalization.TurnFinalizationInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var gate = services.GetRequiredService<Core.Finalization.ITurnJudgmentGate>();
            var decision = await gate.EvaluateAsync(input, cancellationToken).ConfigureAwait(false);

            var enforcementNotice = decision.Action switch
            {
                Core.Finalization.JudgmentEnforcementAction.RequestJudgment =>
                    ActivityNoticeFactory.ForJudgmentRequested(
                        decision.RequestId, decision.Attempts, input.Source ?? "stop_hook"),

                // The gate fails open, so an enforcement failure looks like an ordinary unjudged
                // turn from the outside. Record why, or the difference is invisible.
                _ when decision.Reason.StartsWith(
                    Core.Finalization.TurnJudgmentGate.EnforcementFailedReason, StringComparison.Ordinal) =>
                    ActivityNoticeFactory.ForJudgmentEnforcementFailed(
                        decision.Reason, input.Source ?? "stop_hook"),

                _ => null,
            };

            if (enforcementNotice is not null)
            {
                await services.GetRequiredService<IActivityRecorder>()
                    .RecordAsync(
                        enforcementNotice with { TurnId = Core.Activity.TurnCorrelation.Compute(input.Cwd, input.Prompt) },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return decision;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[agentrecall] judgment enforcement skipped: {ex.Message}");
            return new Core.Finalization.JudgmentGateDecision
            {
                Action = Core.Finalization.JudgmentEnforcementAction.Finalize,
                Reason = Core.Finalization.TurnJudgmentGate.EnforcementFailedReason,
            };
        }
    }

    /// <summary>
    /// Prints what became of the turn's reported outcomes. Refusals are printed, not swallowed: a
    /// reporter that thinks it moved a rule's confidence when it did not will never correct itself.
    /// </summary>
    private static void RenderRuleOutcomes(TextWriter output, Core.Outcomes.TurnOutcomeReportResult report)
    {
        if (report.IsEmpty)
        {
            return;
        }

        output.WriteLine();
        foreach (var (ruleId, outcome) in report.Applied)
        {
            output.WriteLine($"Outcome recorded: #{ruleId} {outcome}");
        }

        foreach (var refusal in report.Rejected)
        {
            output.WriteLine($"Outcome refused: {refusal}");
        }

        if (report.Unreported.Count > 0)
        {
            output.WriteLine(
                $"Awaiting an outcome: {string.Join(", ", report.Unreported.Select(id => $"#{id}"))}");
        }
    }

    /// <summary>
    /// The rules this turn injected that nobody has reported an outcome for yet. Asking the
    /// reporter with no reports is a read: it resolves the turn's injected rules and subtracts the
    /// ones already in the ledger, and writes nothing.
    /// </summary>
    private static async Task<IReadOnlyList<int>> RulesAwaitingOutcomeAsync(
        IServiceProvider services,
        Core.Finalization.TurnFinalizationInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var turnId = Core.Activity.TurnCorrelation.Compute(input.Cwd, input.Prompt);
            var report = await services.GetRequiredService<Core.Outcomes.ITurnOutcomeReporter>()
                .ApplyAsync(turnId, [], cancellationToken).ConfigureAwait(false);

            return report.Unreported;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never let the outcome half break the judgment ask.
            Console.Error.WriteLine($"[agentrecall] could not list rules awaiting an outcome: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Applies the turn's reported rule outcomes and records what came of them. Failures are
    /// swallowed: a rejected or unrecordable outcome must never be the reason a turn cannot end,
    /// and the reports themselves are already validated by the reporter.
    /// </summary>
    private static async Task<Core.Outcomes.TurnOutcomeReportResult> ApplyRuleOutcomesAsync(
        IServiceProvider services,
        Core.Finalization.TurnFinalizationInput input,
        TurnFinalizationResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Core.Outcomes.TurnOutcomeReporting.ApplyAndRecordAsync(
                services.GetRequiredService<Core.Outcomes.ITurnOutcomeReporter>(),
                services.GetRequiredService<IActivityRecorder>(),
                result.TurnId,
                input.RuleOutcomes,
                input.Source ?? "cli",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[agentrecall] rule outcomes not recorded: {ex.Message}");
            return new Core.Outcomes.TurnOutcomeReportResult();
        }
    }

    /// <summary>
    /// Emits Claude Code's Stop-hook block response: the turn does not finish, and
    /// <c>reason</c> — the one channel a blocked Stop has — tells the model which tool to call and
    /// what it must decide. The exit code stays 0; the JSON decision, not the exit code, blocks.
    /// </summary>
    private static void EmitJudgmentBlock(
        TextWriter output,
        Core.Finalization.JudgmentGateDecision decision,
        IReadOnlyList<int>? rulesAwaitingOutcome = null)
    {
        // One message, composed in one place: the stored block reason covers the judgment, and the
        // outcome clause is added only when this turn left rules unreported.
        var reason = rulesAwaitingOutcome is { Count: > 0 }
            ? Core.Finalization.JudgmentBlockMessage.For(decision.RequestId, rulesAwaitingOutcome)
            : decision.BlockReason ?? Core.Finalization.JudgmentBlockMessage.For(decision.RequestId);

        var response = new System.Text.Json.Nodes.JsonObject
        {
            ["decision"] = "block",
            ["reason"] = reason,
            ["hookSpecificOutput"] = new System.Text.Json.Nodes.JsonObject
            {
                ["hookEventName"] = "Stop",
            },
        };

        output.WriteLine(response.ToJsonString());
    }

    /// <summary>
    /// Emits the Stop-hook <c>systemMessage</c> carrying the aggregated Turn Memory
    /// Summary, at the configured <see cref="AgentRecallOptions.ResolvedTurnSummaryLevel"/>.
    /// Stays silent for <c>Silent</c> level and for a no-op turn, so it never spams. The
    /// output is bounded (short titles only, max items per section) so it cannot bloat the
    /// session, and any failure is swallowed so the hook never blocks.
    /// </summary>
    private static async Task EmitTurnSummaryHookAsync(
        IServiceProvider services,
        TextWriter output,
        TurnFinalizationResult result,
        CancellationToken cancellationToken)
    {
        var options = services.GetRequiredService<AgentRecallOptions>();
        var level = options.ResolvedTurnSummaryLevel;
        if (level == TurnSummaryLevel.Silent || !options.FinalizerShowUserNotice)
        {
            return;
        }

        try
        {
            var summaryService = services.GetRequiredService<ITurnSummaryService>();
            var summary = await summaryService.BuildForTurnAsync(result.TurnId, cancellationToken).ConfigureAwait(false);

            // An ordinary turn with no memory activity stays silent (never spam a no-op).
            if (summary.IsEmpty)
            {
                return;
            }

            // A turn whose only news is reinforcing a duplicate changes nothing visible;
            // honour SuppressDuplicateNotices and stay silent (used rules / captures still print).
            if (options.SuppressDuplicateNotices && OnlyReinforcedDuplicate(summary, result))
            {
                return;
            }

            var text = TurnSummaryRenderer.Render(summary, level, "this turn");
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            output.WriteLine(new System.Text.Json.Nodes.JsonObject
            {
                ["systemMessage"] = text,
            }.ToJsonString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block Claude Code; the summary is best-effort.
            Console.Error.WriteLine($"[agentrecall] turn summary skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>turn-summary [--last] [--json] [--detailed|--compact]</c>: print the aggregated
    /// Turn Memory Summary for the current/last turn. <c>--last</c> is the default (and only)
    /// scope today. Without a level flag it follows <c>TurnSummaryLevel</c>, except an
    /// explicit invocation never stays Silent. Always exits 0 so it never blocks an agent.
    /// </summary>
    private static async Task<int> TurnSummaryAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(args);
        var json = options.ContainsKey("json");

        try
        {
            await using var scope = services.CreateAsyncScope();
            await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);

            var summaryService = scope.ServiceProvider.GetRequiredService<ITurnSummaryService>();
            var summary = await summaryService.BuildLastAsync(cancellationToken).ConfigureAwait(false);

            if (json)
            {
                WriteJson(output, TurnSummaryJson(summary));
                return 0;
            }

            var configured = scope.ServiceProvider.GetRequiredService<AgentRecallOptions>().ResolvedTurnSummaryLevel;
            var level = ResolveTurnSummaryLevel(options, configured);
            // An explicit command is never a no-op: surface at least the compact line even
            // when the configured level is Silent.
            if (level == TurnSummaryLevel.Silent)
            {
                level = TurnSummaryLevel.Compact;
            }

            output.WriteLine(TurnSummaryRenderer.Render(summary, level, "the last turn"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block the agent; report the failure on stderr only.
            Console.Error.WriteLine($"[agentrecall] turn-summary failed: {ex.Message}");
            if (json)
            {
                WriteJson(output, TurnSummaryJson(new TurnSummary()));
            }
        }

        return 0;
    }

    /// <summary>
    /// True when the turn's only memory activity was reinforcing an existing rule (a pure
    /// duplicate) — nothing was used, captured, suggested, remembered, ignored, or errored,
    /// and every skip was a duplicate. Uses the structured result, not parsed text.
    /// </summary>
    private static bool OnlyReinforcedDuplicate(TurnSummary summary, TurnFinalizationResult result) =>
        summary.Used.Count == 0 &&
        summary.Remembered.Count == 0 &&
        summary.Ignored.Count == 0 &&
        result.Captured.Count == 0 &&
        result.Suggested.Count == 0 &&
        result.Errors.Count == 0 &&
        result.Skipped.Count > 0 &&
        result.Skipped.All(s => s.DuplicateOfRuleId is not null);

    /// <summary>Resolves the display level from <c>--detailed</c>/<c>--compact</c>, else the configured level.</summary>
    private static TurnSummaryLevel ResolveTurnSummaryLevel(
        IReadOnlyDictionary<string, string> options,
        TurnSummaryLevel fallback)
    {
        if (options.ContainsKey("detailed"))
        {
            return TurnSummaryLevel.Detailed;
        }

        if (options.ContainsKey("compact"))
        {
            return TurnSummaryLevel.Compact;
        }

        return fallback;
    }

    /// <summary>Stable, deterministic JSON for a turn summary (snake_case keys, no timestamp).</summary>
    private static object TurnSummaryJson(TurnSummary summary) =>
        new
        {
            turn_id = summary.TurnId,
            summary = new
            {
                used = summary.Used.Count,
                captured = summary.Captured.Count,
                suggested = summary.Suggested.Count,
                skipped = summary.Skipped.Count,
                remembered = summary.Remembered.Count,
                ignored = summary.Ignored.Count,
                errors = summary.Errors.Count,
            },
            used_rules = summary.Used.Select(UsedRuleJson).ToArray(),
            captured_rules = summary.Captured.Select(CapturedRuleJson).ToArray(),
            suggested_rules = summary.Suggested.Select(CapturedRuleJson).ToArray(),
            skipped_candidates = summary.Skipped
                .Select(s => new { title = s.Title, reason = s.Reason })
                .ToArray(),
            remembered_suggestions = summary.Remembered.Select(SuggestionRuleJson).ToArray(),
            ignored_suggestions = summary.Ignored.Select(SuggestionRuleJson).ToArray(),
            errors = summary.Errors.ToArray(),
        };

    private static object UsedRuleJson(TurnSummaryRule rule) =>
        new { id = rule.Id, title = rule.Title, category = rule.Category };

    private static object CapturedRuleJson(TurnSummaryRule rule) =>
        new { id = rule.Id, title = rule.Title, reason = rule.Reason };

    private static object SuggestionRuleJson(TurnSummaryRule rule) =>
        new { id = rule.Id, title = rule.Title };

    private static void RenderFinalization(TextWriter output, TurnFinalizationResult result) =>
        output.WriteLine(TurnFinalizationFormatter.RenderText(result));

    private static object FinalizationJson(TurnFinalizationResult? result, int? awaitingJudgmentRequestId = null) =>
        new
        {
            awaitingJudgment = awaitingJudgmentRequestId is not null,
            awaitingJudgmentRequestId,
            decisionSource = result?.DecisionSource,
            decision = result?.Decision,
            reason = result?.JudgeReason,
            confidence = result?.JudgeConfidence,
            targetRuleId = result?.TargetRuleId,
            captured = (result?.Captured ?? []).Select(l => new
            {
                ruleId = l.RuleId,
                category = l.Category.ToString(),
                scope = l.ScopeLabel,
                confidence = l.Confidence,
                text = l.Text,
                note = l.Note,
            }).ToArray(),
            suggested = (result?.Suggested ?? []).Select(l => new
            {
                ruleId = l.RuleId,
                category = l.Category.ToString(),
                scope = l.ScopeLabel,
                confidence = l.Confidence,
                text = l.Text,
                note = l.Note,
            }).ToArray(),
            skipped = (result?.Skipped ?? []).Select(s => new
            {
                reason = s.Reason,
                duplicateOfRuleId = s.DuplicateOfRuleId,
            }).ToArray(),
            duplicates = (result?.Duplicates ?? []).ToArray(),
            errors = (result?.Errors ?? []).ToArray(),
        };

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

        output.WriteLine("Type:");
        output.WriteLine(rule.Category.ToString());
        output.WriteLine();

        output.WriteLine("Rule:");
        output.WriteLine(rule.RuleText);
        output.WriteLine();

        // Outcome-aware provenance: why the rule was captured, and the evidence behind it.
        if (rule.CaptureReason != Core.Capture.CaptureReason.None)
        {
            output.WriteLine("Captured because:");
            output.WriteLine(rule.CaptureReason.ToString());
            output.WriteLine();

            if (!string.IsNullOrWhiteSpace(rule.EvidenceSummary))
            {
                output.WriteLine("Evidence:");
                output.WriteLine(rule.EvidenceSummary);
                output.WriteLine();
            }
        }

        output.WriteLine("Scope:");
        output.WriteLine(DescribeRuleScope(rule));
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

    /// <summary>
    /// A human scope label for `rules explain`. A user/communication preference is scoped
    /// to the user (stored Global so it applies everywhere for this user), so it is shown
    /// as "User" rather than "Global"; other rules show their stored scope.
    /// </summary>
    private static string DescribeRuleScope(RecallRule rule)
    {
        if (rule.Category is RuleCategory.UserPreference or RuleCategory.CommunicationPreference)
        {
            return "User (applies to this user everywhere)";
        }

        return rule.ScopeLevel == ScopeLevel.Global
            ? "Global"
            : string.IsNullOrWhiteSpace(rule.ScopeValue)
                ? rule.ScopeLevel.ToString()
                : $"{rule.ScopeLevel}:{rule.ScopeValue}";
    }

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
        output.WriteLine("  claude-code init     Wire AgentRecall's hooks + CLAUDE.md guidance for");
        output.WriteLine("                       Claude Code (no dev container scaffolding)");
        output.WriteLine("  devcontainer init    Same wiring, plus --create scaffolds a dev container");
        output.WriteLine("                       that reinstalls it on every rebuild");
        output.WriteLine("  feedback add         Record feedback and extract a pending rule");
        output.WriteLine("  rules list           List all rules (--status <status>, e.g. Pending)");
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
        output.WriteLine("  finalize-turn        Finalize a completed turn from a Stop-hook payload on stdin:");
        output.WriteLine("                       extract lessons and auto-capture/suggest/skip (--json, --hook)");
        output.WriteLine("  finalize-turn status Show the last finalization result (--json); also --last");
        output.WriteLine("  turn-summary --last  Show the aggregated Turn Memory Summary for the last turn");
        output.WriteLine("                       (--json, --detailed, --compact)");
        output.WriteLine("  activity last        Show the latest AgentRecall activity notice (--json)");
        output.WriteLine("  activity list        Show recent activity notices (--limit <n>, --json)");
        output.WriteLine("  seed list            List built-in seed packs (curated starter rules)");
        output.WriteLine("  seed show <pack>     Show a seed pack's rules, defaults, and provenance");
        output.WriteLine("  seed install <pack>  Install a seed pack (--active, --suggested, --force, --json)");
        output.WriteLine("  seed remove <pack>   Remove an installed seed pack (--force, --json)");
        output.WriteLine("  seed status          Show installed seed packs and rule counts (--json)");
        output.WriteLine("  career impact --last Show the last turn's career-impact candidate (--json, --detailed)");
        output.WriteLine("  career journal --last");
        output.WriteLine("                       Generate a promotion-ready journal entry (--json, --file <path>)");
        output.WriteLine("  career status        Show career-impact pack/mode and the last candidate (--json)");
        output.WriteLine("  document write       Write an offered document (--type, --title, --turn-id, --root, --force, --json;");
        output.WriteLine("                       body read from stdin)");
        output.WriteLine("  document status      Show document-opportunity mode and the last candidate (--json)");
        output.WriteLine("  cleanup pending-noise");
        output.WriteLine("                       Archive noisy Pending rules from AgentRecall's end-of-turn capture (--apply, --json, --tag, --status)");
        output.WriteLine("  doctor               Check database/schema, PATH, Claude Code hook wiring, and the");
        output.WriteLine("                       installed version (--fix, --json, --offline, --project <path>)");
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
