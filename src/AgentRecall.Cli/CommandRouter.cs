using AgentRecall.Core;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Search;
using AgentRecall.Core.Services;
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

            case "feedback":
                return await FeedbackAsync(rest, services, output, logger, cancellationToken).ConfigureAwait(false);

            case "rules":
                return await RulesAsync(rest, services, output, logger, cancellationToken).ConfigureAwait(false);

            case "search":
                return await SearchAsync(rest, services, output, logger, cancellationToken).ConfigureAwait(false);

            case "import":
                return await ImportAsync(rest, services, output, logger, cancellationToken).ConfigureAwait(false);

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
            output.WriteLine($"Recorded feedback as event #{result.Event.Id}.");
            output.WriteLine($"Created {result.Rule.Status} rule #{result.Rule.Id}: {result.Rule.RuleText}");
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
            output.WriteLine("  agentrecall import pr-comments <file> [--task <pr title>] [--scope-level <level>] [--scope-value <text>] [--tags <a,b>]");
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
            output.WriteLine("Usage: agentrecall import pr-comments <file> [--task <pr title>] [--scope-level <level>] [--scope-value <text>] [--tags <a,b>]");
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
        };

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var importer = scope.ServiceProvider.GetRequiredService<IPullRequestImportService>();

        try
        {
            var result = await importer.ImportFileAsync(filePath, importOptions, cancellationToken).ConfigureAwait(false);
            output.WriteLine($"Imported PR comments: {result.CommentsFound} comment(s), {result.RulesCreated} pending rule(s) created, {result.Skipped} skipped.");
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
        output.WriteLine("  feedback add         Record feedback and extract a pending rule");
        output.WriteLine("  rules list           List all rules");
        output.WriteLine("  rules show <id>      Show a single rule in detail");
        output.WriteLine("  rules approve <id>   Move a Pending rule to Active");
        output.WriteLine("  rules promote <id>   Promote a rule");
        output.WriteLine("  rules supersede <oldId> <newId>");
        output.WriteLine("                       Replace one rule with another");
        output.WriteLine("  rules archive <id>   Archive a rule (excluded from search)");
        output.WriteLine("  search \"<query>\"     Search rules by keyword, ranked");
        output.WriteLine("  import build-log <file>");
        output.WriteLine("  import test-log <file>");
        output.WriteLine("  import lint-log <file>");
        output.WriteLine("                       Ingest a failure log into events");
        output.WriteLine("  import pr-comments <file>");
        output.WriteLine("                       Capture PR review comments as pending rules");
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
