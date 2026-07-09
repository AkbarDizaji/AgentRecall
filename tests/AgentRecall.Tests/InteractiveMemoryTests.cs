using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Cli.Mcp.Tools;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Feedback;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for Interactive Memory: surfacing the existing capture decision
/// (AutoCapture / SuggestCapture / Skip) as a lightweight interaction. It never makes
/// the capture decision and never re-classifies worthiness — it only decides how a
/// SuggestCapture is shown, and never blocks a hook or MCP flow.
/// </summary>
public class InteractiveMemoryTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    // A clear, worthy lesson (classifier scores 0.90) — auto vs pending is set by AutoApprove.
    private const string Lesson =
        "When implementing feature gates, ensure frontend and backend use the same definition.";

    private static FeedbackInput Suggest(string feedback = Lesson) => new()
    {
        Task = "work",
        Feedback = feedback,
        ScopeLevel = ScopeLevel.Repository,
        ScopeValue = "skedda",
        AutoApprove = false, // posture off → SuggestCapture (Pending)
    };

    private static FeedbackInput Auto(string feedback = Lesson) => new()
    {
        Task = "work",
        Feedback = feedback,
        ScopeLevel = ScopeLevel.Repository,
        ScopeValue = "skedda",
        AutoApprove = true, // explicit accept → AutoCapture (Active)
    };

    private static Task<InteractiveMemoryOutcome> Handle(
        FeedbackResult result,
        IServiceProvider sp,
        InteractiveMemoryMode mode,
        bool isInteractive,
        string typed,
        TextWriter output) =>
        InteractiveMemory.HandleAsync(result, mode, isInteractive, new StringReader(typed), output, sp);

    // A. The default InteractiveMemoryMode is Auto.
    [Fact]
    public void A_DefaultMode_IsAuto()
    {
        var options = new AgentRecallOptions();
        Assert.Equal("Auto", options.InteractiveMemoryMode);
        Assert.Equal(InteractiveMemoryMode.Auto, options.ResolvedInteractiveMemoryMode);
        Assert.Equal(InteractiveMemoryMode.Auto, InteractiveMemoryModes.Resolve("not-a-mode"));
    }

    // B. Auto mode: an AutoCapture saves automatically and does not prompt.
    [Fact]
    public async Task B_Auto_AutoCapture_NoPrompt()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Auto());
        var output = new StringWriter();

        var outcome = await Handle(result, sp, InteractiveMemoryMode.Auto, isInteractive: true, typed: "", output);

        Assert.Equal(InteractiveMemoryOutcome.AutoCaptured, outcome);
        Assert.DoesNotContain("possible lesson detected", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(RuleStatus.Active, (await sp.GetRequiredService<IRecallRuleRepository>().GetAsync(result.Rule!.Id))!.Status);
    }

    // C. Auto mode: a SuggestCapture prompts when the CLI is interactive.
    [Fact]
    public async Task C_Auto_SuggestCapture_PromptsWhenInteractive()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Suggest());
        Assert.Equal(CaptureOutcome.SuggestCapture, result.Decision!.Outcome);

        var output = new StringWriter();
        var outcome = await Handle(result, sp, InteractiveMemoryMode.Auto, isInteractive: true, typed: "y\n", output);

        var text = output.ToString();
        Assert.Contains("possible lesson detected", text, StringComparison.Ordinal);
        Assert.Contains("[y] Remember", text, StringComparison.Ordinal);
        Assert.Equal(InteractiveMemoryOutcome.Remembered, outcome);
    }

    // D. Auto mode: a SuggestCapture does not prompt in non-interactive mode and shows the
    //    pending follow-up command.
    [Fact]
    public async Task D_Auto_SuggestCapture_NonInteractive_ShowsFollowup()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Suggest());
        var output = new StringWriter();

        var outcome = await Handle(result, sp, InteractiveMemoryMode.Auto, isInteractive: false, typed: "", output);

        var text = output.ToString();
        Assert.Equal(InteractiveMemoryOutcome.PendingSuggested, outcome);
        Assert.DoesNotContain("[y] Remember", text, StringComparison.Ordinal);
        Assert.Contains($"rules approve {result.Rule!.Id}", text, StringComparison.Ordinal);
        Assert.Equal(RuleStatus.Pending, (await sp.GetRequiredService<IRecallRuleRepository>().GetAsync(result.Rule.Id))!.Status);
    }

    // D (CLI). `feedback add --pending` under a non-interactive runner prints the follow-up.
    [Fact]
    public async Task D_Cli_FeedbackAdd_Pending_PrintsFollowup()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(
            ["feedback", "add", "--task", "work", "--feedback", Lesson, "--scope-level", "Repository", "--scope-value", "skedda", "--pending"],
            db.Services, output);

        Assert.Equal(0, code);
        Assert.Contains("rules approve", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("[y] Remember", output.ToString(), StringComparison.Ordinal);
    }

    // E. Ask mode: a SuggestCapture prompts.
    [Fact]
    public async Task E_Ask_SuggestCapture_Prompts()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Suggest());
        var output = new StringWriter();

        var outcome = await Handle(result, sp, InteractiveMemoryMode.Ask, isInteractive: true, typed: "n\n", output);

        Assert.Contains("possible lesson detected", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(InteractiveMemoryOutcome.Ignored, outcome);
    }

    // F. Silent mode: a SuggestCapture does not prompt.
    [Fact]
    public async Task F_Silent_SuggestCapture_NoPrompt()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Suggest());
        var output = new StringWriter();

        var outcome = await Handle(result, sp, InteractiveMemoryMode.Silent, isInteractive: true, typed: "y\n", output);

        Assert.Equal(InteractiveMemoryOutcome.PendingSuggested, outcome);
        Assert.DoesNotContain("[y] Remember", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(RuleStatus.Pending, (await sp.GetRequiredService<IRecallRuleRepository>().GetAsync(result.Rule!.Id))!.Status);
    }

    // G. Hook mode: a SuggestCapture never blocks and creates a pending rule with a
    //    follow-up command in the compact summary.
    [Fact]
    public async Task G_HookMode_SuggestCapture_NeverBlocks_CreatesPending()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var finalizer = scope.ServiceProvider.GetRequiredService<ITurnFinalizer>();

        // A mid-confidence judge verdict → SuggestCapture (Pending).
        var result = await finalizer.FinalizeAsync(new TurnFinalizationInput
        {
            Prompt = "Don't re-query what you already loaded.",
            Source = "stop_hook",
            Cwd = "/repo/project",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "project",
            SuppliedJudgment = JudgeVerdicts.Suggest(),
        });

        var lesson = Assert.Single(result.Suggested);
        var rule = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().GetAsync(lesson.RuleId);
        Assert.Equal(RuleStatus.Pending, rule!.Status);

        var summary = TurnFinalizationFormatter.SummaryLine(result);
        Assert.Contains($"rules approve {lesson.RuleId}", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", summary, StringComparison.Ordinal); // compact, single line
    }

    // H + T. MCP: a SuggestCapture returns a structured response with suggested actions and
    //    no terminal prompt text, and never blocks.
    [Fact]
    public async Task H_Mcp_SuggestCapture_StructuredResponse()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var args = new JsonObject
        {
            ["feedback"] = Lesson,
            ["scope_level"] = "Repository",
            ["scope_value"] = "skedda",
            ["pending"] = true, // force SuggestCapture
        };

        var node = await new CaptureFeedbackTool().InvokeAsync(args, db.Services, CancellationToken.None);

        Assert.Equal("SuggestCapture", node!["capture_decision"]!.GetValue<string>());
        Assert.NotNull(node["pending_rule_id"]);
        var actions = node["suggested_actions"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Contains("approve", actions);
        Assert.Contains("reject", actions);
        Assert.Contains("view_details", actions);

        // No terminal prompt text leaks into the structured payload.
        var json = node.ToJsonString();
        Assert.DoesNotContain("[y] Remember", json, StringComparison.Ordinal);
        Assert.DoesNotContain("possible lesson detected", json, StringComparison.Ordinal);
    }

    // I. The user chooses y: the pending rule becomes Active.
    [Fact]
    public async Task I_ChooseY_RuleBecomesActive()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Suggest());
        var output = new StringWriter();

        var outcome = await Handle(result, sp, InteractiveMemoryMode.Auto, isInteractive: true, typed: "y\n", output);

        Assert.Equal(InteractiveMemoryOutcome.Remembered, outcome);
        Assert.Equal(RuleStatus.Active, (await sp.GetRequiredService<IRecallRuleRepository>().GetAsync(result.Rule!.Id))!.Status);
        Assert.Contains($"remembered rule #{result.Rule.Id}", output.ToString(), StringComparison.Ordinal);
    }

    // J. The user chooses n: the pending rule becomes Archived.
    [Fact]
    public async Task J_ChooseN_RuleBecomesArchived()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Suggest());
        var output = new StringWriter();

        var outcome = await Handle(result, sp, InteractiveMemoryMode.Auto, isInteractive: true, typed: "n\n", output);

        Assert.Equal(InteractiveMemoryOutcome.Ignored, outcome);
        Assert.Equal(RuleStatus.Archived, (await sp.GetRequiredService<IRecallRuleRepository>().GetAsync(result.Rule!.Id))!.Status);
    }

    // K. The user chooses v: details are displayed and the prompt repeats.
    [Fact]
    public async Task K_ChooseV_ShowsDetails_ThenRepeats()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Suggest());
        var output = new StringWriter();

        var outcome = await Handle(result, sp, InteractiveMemoryMode.Auto, isInteractive: true, typed: "v\ny\n", output);

        var text = output.ToString();
        Assert.Contains("Details:", text, StringComparison.Ordinal);
        Assert.Contains("Confidence:", text, StringComparison.Ordinal);
        Assert.Contains("Scope:", text, StringComparison.Ordinal);
        // The actions are shown a second time after details.
        Assert.True(CountOccurrences(text, "[y] Remember") >= 2);
        Assert.Equal(InteractiveMemoryOutcome.Remembered, outcome);
    }

    // L. Invalid input is re-prompted with a retry limit, then falls back to pending.
    [Fact]
    public async Task L_InvalidInput_RepromptsThenFallsBack()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Suggest());
        var output = new StringWriter();

        var outcome = await Handle(result, sp, InteractiveMemoryMode.Auto, isInteractive: true, typed: "x\nz\nq\n", output);

        Assert.Contains("Please choose y, n, or v.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(InteractiveMemoryOutcome.PendingSuggested, outcome);
        Assert.Equal(RuleStatus.Pending, (await sp.GetRequiredService<IRecallRuleRepository>().GetAsync(result.Rule!.Id))!.Status);
    }

    // M. A duplicate SuggestCapture does not create a second pending rule or prompt.
    [Fact]
    public async Task M_DuplicateSuggestCapture_NoDuplicate()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;
        var service = sp.GetRequiredService<IFeedbackService>();

        await service.AddAsync(Suggest());
        var second = await service.AddAsync(Suggest());
        Assert.True(second.ReusedExistingRule);

        var output = new StringWriter();
        var outcome = await Handle(second, sp, InteractiveMemoryMode.Auto, isInteractive: true, typed: "y\n", output);

        Assert.Equal(InteractiveMemoryOutcome.ReusedDuplicate, outcome);
        Assert.DoesNotContain("possible lesson detected", output.ToString(), StringComparison.Ordinal);
        Assert.Single(await sp.GetRequiredService<IRecallRuleRepository>().ListAsync());
    }

    // N. The AutoCapture notice uses the AgentRecall badge.
    [Fact]
    public void N_AutoCaptureNotice_UsesBadge()
    {
        var rule = new RecallRule { Id = 28, RuleText = "Preserve else semantics when flattening nested template conditionals." };
        var result = new FeedbackResult(null, rule)
        {
            Decision = new CaptureDecision(CaptureOutcome.AutoCapture, "reason", 0.9, ScopeLevel.Repository, "skedda", "notice"),
        };

        var rendered = ActivityNoticeRenderer.Render(ActivityNoticeFactory.ForFeedback(result, "cli")!, NoticeLevel.Normal)!;
        Assert.StartsWith("🧠 **AgentRecall:**", rendered);
        Assert.Contains("captured 1 new rule", rendered, StringComparison.Ordinal);
    }

    // O. The SuggestCapture prompt uses the badge and the y/n/v actions.
    [Fact]
    public async Task O_SuggestPrompt_UsesBadgeAndActions()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Suggest());
        var output = new StringWriter();
        await Handle(result, sp, InteractiveMemoryMode.Auto, isInteractive: true, typed: "n\n", output);

        var text = output.ToString();
        Assert.Contains("🧠 **AgentRecall:**", text, StringComparison.Ordinal);
        Assert.Contains("[y] Remember", text, StringComparison.Ordinal);
        Assert.Contains("[n] Ignore", text, StringComparison.Ordinal);
        Assert.Contains("[v] View details", text, StringComparison.Ordinal);
    }

    // P. A Skip never prompts.
    [Fact]
    public async Task P_Skip_NeverPrompts()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        // A bare code fact → Skip, no rule.
        var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(new FeedbackInput
        {
            Task = "work",
            Feedback = "Use IsEventsFeatureEnabled.",
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "skedda",
        });
        Assert.Equal(CaptureOutcome.Skip, result.Decision!.Outcome);

        var output = new StringWriter();
        var outcome = await Handle(result, sp, InteractiveMemoryMode.Auto, isInteractive: true, typed: "y\n", output);

        Assert.Equal(InteractiveMemoryOutcome.Skipped, outcome);
        Assert.DoesNotContain("possible lesson detected", output.ToString(), StringComparison.Ordinal);
    }

    // Q. capture-status includes pending suggestions.
    [Fact]
    public async Task Q_CaptureStatus_ShowsPendingSuggestion()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ITurnFinalizer>().FinalizeAsync(new TurnFinalizationInput
            {
                Prompt = "Don't re-query what you already loaded.",
                Source = "stop_hook",
                Cwd = "/repo/project",
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "project",
                SuppliedJudgment = JudgeVerdicts.Suggest(),
            });
        }

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["capture-status", "--last-turn"], db.Services, output);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("Pending rule", text, StringComparison.Ordinal);
        Assert.Contains("rules approve", text, StringComparison.Ordinal);
    }

    // R. The CLAUDE.md scaffold forbids "Want me to save it?" and instructs presenting the
    //    Interactive Memory options.
    [Fact]
    public void R_ClaudeMdScaffold_ForbidsWantMeToSaveIt()
    {
        var guidance = AgentRecall.Cli.Devcontainer.DevcontainerScaffolder.ClaudeMdGuidance;
        Assert.Contains("Interactive Memory", guidance, StringComparison.Ordinal);
        Assert.Contains("Want me to save it?", guidance, StringComparison.Ordinal); // present, as a forbidden example
        Assert.Contains("Reply `remember` to save it or `ignore` to skip", guidance, StringComparison.Ordinal);
    }

    // S. The README documents Interactive Memory modes and commands.
    [Fact]
    public void S_Readme_DocumentsInteractiveMemory()
    {
        var readme = File.ReadAllText(FindRepoFile("README.md"));
        Assert.Contains("## Interactive Memory", readme, StringComparison.Ordinal);
        Assert.Contains("InteractiveMemoryMode", readme, StringComparison.Ordinal);
        Assert.Contains("rules list --status Pending", readme, StringComparison.Ordinal);
        Assert.Contains("[y] Remember", readme, StringComparison.Ordinal);
    }

    // W. Auto mode asks only for SuggestCapture, not AutoCapture — even when interactive.
    [Fact]
    public async Task W_Auto_DoesNotAskForAutoCapture_EvenInteractive()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Auto());
        Assert.Equal(CaptureOutcome.AutoCapture, result.Decision!.Outcome);

        var output = new StringWriter();
        var outcome = await Handle(result, sp, InteractiveMemoryMode.Auto, isInteractive: true, typed: "n\n", output);

        Assert.Equal(InteractiveMemoryOutcome.AutoCaptured, outcome);
        Assert.DoesNotContain("[y] Remember", output.ToString(), StringComparison.Ordinal);
    }

    // X. An activity notice is recorded for a remembered (and an ignored) suggestion.
    [Fact]
    public async Task X_ActivityRecorded_ForRememberedAndIgnored()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using (var scope = db.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Suggest());
            await Handle(result, sp, InteractiveMemoryMode.Auto, isInteractive: true, typed: "y\n", new StringWriter());

            var last = await sp.GetRequiredService<IActivityRecorder>().GetLastAsync();
            Assert.Equal(ActivityType.SuggestionRemembered, last!.ActivityType);
            Assert.Contains("Remembered by user from Interactive Memory prompt.", last.Details ?? string.Empty, StringComparison.Ordinal);
        }

        await using (var scope = db.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Suggest("Always wrap external HTTP calls in a timeout."));
            await Handle(result, sp, InteractiveMemoryMode.Auto, isInteractive: true, typed: "n\n", new StringWriter());

            var last = await sp.GetRequiredService<IActivityRecorder>().GetLastAsync();
            Assert.Equal(ActivityType.SuggestionIgnored, last!.ActivityType);
        }
    }

    // Ask mode downgrades a borderline auto-capture (no outcome evidence, modest confidence)
    // to a question; the rule ends up Pending until the user decides.
    [Fact]
    public async Task Ask_BorderlineAutoCapture_IsDowngradedToPrompt()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        // "use parameterized queries" → worthy but generic (confidence 0.5, no evidence) →
        // AutoCapture under the default posture.
        var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Auto("use parameterized queries"));
        Assert.Equal(CaptureOutcome.AutoCapture, result.Decision!.Outcome);

        var output = new StringWriter();
        var outcome = await Handle(result, sp, InteractiveMemoryMode.Ask, isInteractive: true, typed: "y\n", output);

        Assert.Contains("possible lesson detected", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(InteractiveMemoryOutcome.Remembered, outcome);
    }

    // Y. The hook summary stays compact (single line, no full prompt) so it cannot bloat
    //    model context.
    [Fact]
    public void Y_HookSummary_StaysCompact()
    {
        var result = new TurnFinalizationResult
        {
            Suggested = [new FinalizedLesson
            {
                RuleId = 31,
                Category = RuleCategory.RepositoryConvention,
                Text = "Preserve else semantics when flattening nested conditionals.",
                ScopeLabel = "Repository:skedda",
            }],
        };

        var summary = TurnFinalizationFormatter.SummaryLine(result);
        Assert.DoesNotContain("\n", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("[y] Remember", summary, StringComparison.Ordinal);
        Assert.Contains("rules approve 31", summary, StringComparison.Ordinal);
    }

    // Z. Interactive Memory does not change worthiness classification; it only surfaces it.
    [Fact]
    public async Task Z_DoesNotChangeWorthiness()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var result = await sp.GetRequiredService<IFeedbackService>().AddAsync(Suggest());
        var verdictBefore = result.Worthiness!.Verdict;
        var categoryBefore = result.Rule!.Category;

        // Silent surfacing must not approve, archive, or re-classify anything.
        await Handle(result, sp, InteractiveMemoryMode.Silent, isInteractive: true, typed: "y\n", new StringWriter());

        var reloaded = await sp.GetRequiredService<IRecallRuleRepository>().GetAsync(result.Rule.Id);
        Assert.Equal(RuleStatus.Pending, reloaded!.Status);
        Assert.Equal(categoryBefore, reloaded.Category);
        Assert.Equal(verdictBefore, result.Worthiness.Verdict);
    }

    // U. The harness is isolated to a temp directory, never the real home store.
    [Fact]
    public async Task U_TestDatabase_IsIsolated()
    {
        await using var db = new TestDatabase();
        Assert.StartsWith(Path.GetTempPath(), db.Options.DataDirectory);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.DoesNotContain(Path.Combine(home, ".agentrecall"), db.Options.DatabasePath);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string FindRepoFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = dir; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate {fileName} above {AppContext.BaseDirectory}.");
    }
}
