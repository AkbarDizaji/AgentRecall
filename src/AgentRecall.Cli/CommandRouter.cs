using AgentRecall.Core;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Evaluation;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Search;
using AgentRecall.Core.Services;
using AgentRecall.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

            switch (result.HookOutcome)
            {
                case Devcontainer.HookSetupOutcome.Created:
                    output.WriteLine($"Wrote {result.ClaudeSettingsPath} with the UserPromptSubmit hook (automatic rule injection).");
                    break;
                case Devcontainer.HookSetupOutcome.Merged:
                    output.WriteLine($"Added the UserPromptSubmit hook to {result.ClaudeSettingsPath} (automatic rule injection).");
                    break;
                case Devcontainer.HookSetupOutcome.AlreadyPresent:
                    output.WriteLine($"UserPromptSubmit hook already present in {result.ClaudeSettingsPath}; left it as is.");
                    break;
                case Devcontainer.HookSetupOutcome.SettingsUnparseable:
                    output.WriteLine($"Could not parse {result.ClaudeSettingsPath}; left it untouched.");
                    output.WriteLine($"Add this hook manually: a UserPromptSubmit command hook running \"{Devcontainer.DevcontainerScaffolder.HookCommand}\".");
                    break;
            }

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
                // Rejected by the memory-worthiness policy: a low-value code fact.
                var reason = result.Worthiness?.Reason ?? "not memory-worthy";
                output.WriteLine($"Not stored: {reason}");
                output.WriteLine("Store a reusable lesson instead, or pass --feedback with the broader rule.");
                return 0;
            }

            if (result.Event is not null)
            {
                output.WriteLine($"Recorded feedback as event #{result.Event.Id}.");
            }

            var verb = result.ReusedExistingRule ? "Reused existing" : "Created";
            output.WriteLine($"{verb} {result.Rule.Status} rule #{result.Rule.Id}: {result.Rule.RuleText}");
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

            default:
                output.WriteLine("Usage:");
                output.WriteLine("  agentrecall rules list");
                output.WriteLine("  agentrecall rules show <id>");
                output.WriteLine("  agentrecall rules approve <id>");
                output.WriteLine("  agentrecall rules promote <id>");
                output.WriteLine("  agentrecall rules supersede <oldId> <newId>");
                output.WriteLine("  agentrecall rules archive <id>");
                return 1;
        }
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

        WriteBucket(output, "Must-follow", result.MustFollow, showReason: true);
        WriteBucket(output, "Warnings", result.Warnings, showReason: false);

        WriteList(output, "Preferred patterns", ContextProjection.PreferredPatterns(result));
        WriteList(output, "Anti-patterns", ContextProjection.AntiPatterns(result));

        var ids = ContextProjection.SourceRuleIds(result);
        output.WriteLine($"Source rule IDs: {string.Join(", ", ids.Select(id => $"#{id}"))}");
        return 0;
    }

    private static void WriteBucket(TextWriter output, string title, IReadOnlyList<Core.Context.InjectedRule> rules, bool showReason)
    {
        if (rules.Count == 0)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine($"{title}:");
        foreach (var injected in rules)
        {
            output.WriteLine($"  #{injected.Rule.Id} [score {injected.Score:0.00}] {Truncate(injected.Rule.RuleText, 90)}");
            if (showReason && !string.IsNullOrWhiteSpace(injected.Rule.TechnicalContext))
            {
                output.WriteLine($"      reason: {Truncate(injected.Rule.TechnicalContext, 90)}");
            }
        }
    }

    private static void WriteList(TextWriter output, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine($"{title}:");
        foreach (var item in items)
        {
            output.WriteLine($"  - {Truncate(item, 100)}");
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
        if (args.Length == 0 || args[0] != "user-prompt-submit")
        {
            output.WriteLine("Usage: agentrecall hook user-prompt-submit");
            output.WriteLine("(reads the Claude Code hook payload on stdin; intended for a UserPromptSubmit hook)");
            return 1;
        }

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
