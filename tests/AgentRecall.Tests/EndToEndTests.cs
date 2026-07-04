using System.Text.Json;
using AgentRecall.Cli;
using AgentRecall.Cli.Devcontainer;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// End-to-end tests proving AgentRecall doesn't just store rules but recalls and
/// injects them the way Claude Code would receive them, and that
/// <c>devcontainer init</c> wires the integration safely and idempotently.
///
/// All tests are deterministic and offline: no Claude Code, no NuGet, no network,
/// no real container build. The hook tests drive the actual CLI command path
/// (stdin in, stdout out); the rest exercise services/scaffolders directly.
///
/// The hook cases live in one class so they never run in parallel with each other
/// — they redirect the process-global <see cref="Console.In"/>.
/// </summary>
[Collection("ConsoleStdin")]
public class HookInjectionE2ETests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    /// <summary>Adds the Moq rule, then promotes it through the lifecycle service.</summary>
    private static async Task SeedAndPromoteMoqRule(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        var added = await repo.AddAsync(new RecallRule
        {
            Trigger = "writing Moq unit tests",
            RuleText = "Always use It.IsAny<T>() matchers when the argument value is not important.",
            Mistake = "",
            TechnicalContext = "",
            Tags = "moq,tests,testing,matchers",
            Confidence = 0.9,
            Status = RuleStatus.Active,
            ScopeLevel = ScopeLevel.Global,
            ScopeValue = "",
        });

        var lifecycle = scope.ServiceProvider.GetRequiredService<IRuleLifecycleService>();
        var promoted = await lifecycle.PromoteAsync(added.Id);
        Assert.Equal(RuleStatus.Promoted, promoted.Status);
    }

    private static string Payload(string prompt, string cwd) =>
        $$"""{"prompt": {{JsonSerializer.Serialize(prompt)}}, "cwd": {{JsonSerializer.Serialize(cwd)}}}""";

    /// <summary>
    /// Runs the real CLI command <c>hook user-prompt-submit</c> with the payload on
    /// stdin, returning what it would print to Claude Code on stdout.
    /// </summary>
    private static async Task<string> RunHookCli(TestDatabase db, string payload)
    {
        var originalIn = Console.In;
        var output = new StringWriter();
        try
        {
            Console.SetIn(new StringReader(payload));
            var code = await CommandRouter.RunAsync(["hook", "user-prompt-submit"], db.Services, output);
            Assert.Equal(0, code); // the hook must never block the prompt
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        return output.ToString();
    }

    [Fact]
    public async Task Hook_DevelopmentPrompt_InjectsMoqRuleAsClaudeWouldReceiveIt()
    {
        using var repo = new TempRepo();
        await using var db = new TestDatabase();
        await Init(db);
        await SeedAndPromoteMoqRule(db);

        var output = await RunHookCli(
            db, Payload("Write unit tests for OrderService using Moq", repo.Path));

        // The block Claude Code prepends to the model context.
        Assert.Contains("## AgentRecall Technical Context", output);
        Assert.Contains("It.IsAny<T>()", output);

        // The rule surfaces under a strong guidance section. Source Rules is always
        // emitted when any rule is injected; Must Follow / Preferred Patterns appear
        // when the ranker buckets it there.
        Assert.Contains("Source Rules:", output);
        Assert.True(
            output.Contains("Must Follow:") || output.Contains("Preferred Patterns:"),
            "Expected the rule under Must Follow or Preferred Patterns.\n" + output);
    }

    [Fact]
    public async Task Hook_NonDevelopmentPrompt_OutputsNothing()
    {
        using var repo = new TempRepo();
        await using var db = new TestDatabase();
        await Init(db);
        await SeedAndPromoteMoqRule(db);

        var output = await RunHookCli(db, Payload("Write a poem about cats", repo.Path));

        Assert.Equal(string.Empty, output);
    }
}

/// <summary>
/// E2E coverage for the dev container scaffolder and accepted-PR-comment capture.
/// These don't touch Console, so they may run in parallel with everything else.
/// </summary>
public class DevcontainerAndCaptureE2ETests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    // ---- 3. devcontainer init idempotency -------------------------------------

    [Fact]
    public void DevcontainerInit_RunTwice_IsIdempotentAndPreservesContent()
    {
        using var repo = new TempRepo();

        const string existingClaudeMd = "# My Project\n\nProject-specific notes that must survive.\n";
        File.WriteAllText(Path.Combine(repo.Path, "CLAUDE.md"), existingClaudeMd);

        DevcontainerScaffolder.Init(repo.Path, createDevcontainer: true);
        DevcontainerScaffolder.Init(repo.Path, createDevcontainer: true);

        var script = Path.Combine(repo.Path, DevcontainerScaffolder.PostCreateRelativePath);
        var settings = Path.Combine(repo.Path, DevcontainerScaffolder.ClaudeSettingsRelativePath);
        var claudeMd = Path.Combine(repo.Path, DevcontainerScaffolder.ClaudeMdRelativePath);

        Assert.True(File.Exists(script));
        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(claudeMd));

        // The hook command appears exactly once across the settings file.
        Assert.Equal(1, Occurrences(File.ReadAllText(settings), DevcontainerScaffolder.HookCommand));

        // The guidance block appears exactly once, and prior content is preserved.
        var claudeText = File.ReadAllText(claudeMd);
        Assert.Equal(1, Occurrences(claudeText, DevcontainerScaffolder.ClaudeMdHeading));
        Assert.StartsWith(existingClaudeMd, claudeText);
    }

    // ---- 4. existing devcontainer safety --------------------------------------

    [Fact]
    public void DevcontainerInit_WithExistingManifest_DoesNotOverwriteAndReturnsMergeSteps()
    {
        using var repo = new TempRepo();

        var manifestPath = Path.Combine(repo.Path, DevcontainerScaffolder.DevcontainerJsonRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        const string customJsonc = """
        {
          // hand-maintained dev container
          "name": "my-custom-box",
          "image": "ghcr.io/acme/devbox:latest"
        }
        """;
        File.WriteAllText(manifestPath, customJsonc);

        var result = DevcontainerScaffolder.Init(repo.Path);

        // The manifest is untouched, byte for byte.
        Assert.False(result.CreatedDevcontainerJson);
        Assert.Equal(customJsonc, File.ReadAllText(manifestPath));

        // Merge instructions are returned for the user to apply by hand.
        Assert.NotNull(result.ManualSteps);
        Assert.Contains("postCreateCommand", result.ManualSteps);

        // The setup script is still written (it never clobbers an existing manifest).
        Assert.True(File.Exists(Path.Combine(repo.Path, DevcontainerScaffolder.PostCreateRelativePath)));
    }

    // ---- 6. rebuild persistence (script content, no real rebuild) -------------

    [Fact]
    public void PostCreateScript_ContainsTheRebuildSurvivalSteps()
    {
        var script = DevcontainerScaffolder.PostCreateScript;

        Assert.Contains("dotnet tool update --global AgentRecall", script);
        Assert.Contains("\"$AGENTRECALL_BIN\" init", script);
        // Registered by absolute path so the MCP server starts regardless of PATH.
        Assert.Contains("claude mcp add agentrecall \"$AGENTRECALL_BIN\" mcp", script);
        Assert.Contains("\"$AGENTRECALL_BIN\" --version", script);
        Assert.Contains("AgentRecall ready", script);
    }

    // ---- 5. accepted PR comment capture ---------------------------------------

    [Fact]
    public async Task ImportPrComment_Accepted_CreatesActiveRepositoryScopedRuleAndEvent()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<IPullRequestImportService>();

        var result = await importer.ImportAsync(
            [new() { Body = "Always forward the custom port in login redirects." }],
            new PullRequestImportOptions
            {
                Accepted = true,
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "skedda",
            });

        Assert.Equal(1, result.RulesCreated);

        var rules = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync();
        var rule = Assert.Single(rules);
        Assert.Equal(RuleStatus.Active, rule.Status);
        Assert.Equal(ScopeLevel.Repository, rule.ScopeLevel);
        Assert.Equal("skedda", rule.ScopeValue);

        // The capture also records an audit event.
        var events = await scope.ServiceProvider.GetRequiredService<IRecallEventRepository>().ListAsync();
        Assert.NotEmpty(events);
    }

    [Fact]
    public async Task ImportPrComment_NotAccepted_CreatesPendingRule()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<IPullRequestImportService>();

        await importer.ImportAsync(
            [new() { Body = "Always forward the custom port in login redirects." }],
            new PullRequestImportOptions { ScopeLevel = ScopeLevel.Repository, ScopeValue = "skedda" });

        var rules = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync();
        var rule = Assert.Single(rules);
        Assert.Equal(RuleStatus.Pending, rule.Status);
    }

    private static int Occurrences(string haystack, string needle) =>
        haystack.Split(needle).Length - 1;
}

/// <summary>A throwaway directory with a <c>.git</c> marker, cleaned up on dispose.</summary>
internal sealed class TempRepo : IDisposable
{
    public string Path { get; }

    public TempRepo()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "agentrecall-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
