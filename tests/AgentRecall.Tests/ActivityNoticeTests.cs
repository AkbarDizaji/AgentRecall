using System.Text.Json;
using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Conflicts;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Mining;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Activity Notices: AgentRecall is visible by default, but the human-facing notices
/// must never bloat the model-visible context. These tests prove both halves — rich
/// CLI/status output, and compact hook/model output that never repeats rule text.
/// </summary>
[Collection("ConsoleStdin")]
public class ActivityNoticeTests
{
    private const string Badge = "🧠 **AgentRecall:**";

    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    // ---- Config defaults & invalid-value safety (A, B, Y) ---------------------

    [Fact] // A
    public void DefaultActivityNoticeLevel_IsVerbose() =>
        Assert.Equal(NoticeLevel.Verbose, new AgentRecallOptions().ResolvedActivityNoticeLevel);

    [Fact] // B
    public void DefaultHookNoticeLevel_IsNormal() =>
        Assert.Equal(NoticeLevel.Normal, new AgentRecallOptions().ResolvedHookNoticeLevel);

    [Fact] // Y
    public void InvalidActivityNoticeLevel_FallsBackToVerbose()
    {
        var options = new AgentRecallOptions { ActivityNoticeLevel = "loud-please" };
        Assert.Equal(NoticeLevel.Verbose, options.ResolvedActivityNoticeLevel);
        Assert.False(NoticeLevels.IsValid("loud-please"));
        Assert.True(NoticeLevels.IsValid("Verbose"));
        Assert.True(NoticeLevels.IsValid(null));
    }

    [Fact] // Y (hook): Verbose is clamped to Normal so the hook never goes verbose.
    public void HookNoticeLevel_VerboseIsClampedToNormal()
    {
        var options = new AgentRecallOptions { HookNoticeLevel = "Verbose" };
        Assert.Equal(NoticeLevel.Normal, options.ResolvedHookNoticeLevel);
    }

    // ---- Renderer: levels (C, D, E) -------------------------------------------

    private static ActivityNotice SampleFetch() => new()
    {
        Type = ActivityType.ContextFetched,
        Summary = "fetched 3 relevant rules.",
        Details = ["#12 Moq matcher convention", "#18 Feature gate consistency", "#24 Validator auth/scope safety"],
        RuleIds = [12, 18, 24],
    };

    [Fact] // C
    public void Verbose_IncludesBadgeIdsAndDetailBullets()
    {
        var rendered = ActivityNoticeRenderer.Render(SampleFetch(), NoticeLevel.Verbose)!;

        Assert.Contains("🧠", rendered, StringComparison.Ordinal);
        Assert.Contains("**AgentRecall:**", rendered, StringComparison.Ordinal);
        Assert.Contains("#12", rendered, StringComparison.Ordinal);
        Assert.Contains("\n- #24 Validator auth/scope safety", rendered, StringComparison.Ordinal);
    }

    [Fact] // D
    public void Normal_HasSummaryButNoDetailBullets()
    {
        var rendered = ActivityNoticeRenderer.Render(SampleFetch(), NoticeLevel.Normal)!;

        Assert.StartsWith($"{Badge} fetched 3 relevant rules.", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\n- ", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Moq matcher convention", rendered, StringComparison.Ordinal);
    }

    [Fact] // E
    public void Silent_EmitsNoNotice()
    {
        Assert.Null(ActivityNoticeRenderer.Render(SampleFetch(), NoticeLevel.Silent));
        Assert.Null(ActivityNoticeRenderer.RenderCompact(SampleFetch(), NoticeLevel.Silent));
    }

    [Fact] // Compact never carries bullets, regardless of detail.
    public void Compact_IsSingleLineWithoutDetail()
    {
        var rendered = ActivityNoticeRenderer.RenderCompact(SampleFetch(), NoticeLevel.Normal)!;

        Assert.Equal($"{Badge} fetched 3 relevant rules.", rendered);
        Assert.DoesNotContain("\n", rendered, StringComparison.Ordinal);
    }

    // ---- Factory: skip/duplicate/code-fact visibility (L, M) ------------------

    [Fact] // L: a duplicate skip is summarised, and the reason shows only in Verbose.
    public void DuplicateSkip_ShownInVerbose()
    {
        var result = new FeedbackResult(null, RuleStub())
        {
            ReusedExistingRule = true,
            Decision = Decision(CaptureOutcome.Skip, "duplicate of an existing rule"),
        };
        var notice = ActivityNoticeFactory.ForFeedback(result, "cli")!;

        Assert.Equal(ActivityType.CandidateSkipped, notice.Type);
        Assert.Contains("duplicate", ActivityNoticeRenderer.Render(notice, NoticeLevel.Verbose)!, StringComparison.Ordinal);
        Assert.Contains("reinforced an existing rule", ActivityNoticeRenderer.Render(notice, NoticeLevel.Verbose)!, StringComparison.Ordinal);
        // Normal keeps the summary but drops the reason bullet.
        Assert.DoesNotContain("reinforced an existing rule", ActivityNoticeRenderer.Render(notice, NoticeLevel.Normal)!, StringComparison.Ordinal);
    }

    [Fact] // M: a code-fact skip reason appears only in Verbose.
    public void CodeFactSkip_ReasonOnlyInVerbose()
    {
        var result = new FeedbackResult(null, null)
        {
            Decision = Decision(CaptureOutcome.Skip, "code fact recoverable from the repository"),
        };
        var notice = ActivityNoticeFactory.ForFeedback(result, "cli")!;

        Assert.Contains("code fact", ActivityNoticeRenderer.Render(notice, NoticeLevel.Verbose)!, StringComparison.Ordinal);
        Assert.DoesNotContain("code fact", ActivityNoticeRenderer.Render(notice, NoticeLevel.Normal)!, StringComparison.Ordinal);
    }

    // ---- Factory: conflict only when it affects output (N) --------------------

    [Fact] // N
    public void ConflictNotice_OnlyWhenConflictExists()
    {
        Assert.Null(ActivityNoticeFactory.ForConflictResolved([], "cli"));

        var resolved = new ResolvedConflict
        {
            Conflict = new RuleConflict
            {
                ConflictId = "8-12",
                RuleIds = [8, 12],
                ConflictType = RuleConflictType.BroaderVsSpecific,
                Summary = "broad vs specific",
                DetectedReason = "overlap",
            },
            Resolution = new RuleResolution
            {
                SelectedRuleId = 12,
                IgnoredRuleIds = [8],
                ScoreBreakdown = [],
                Explanation = ["repo-scoped beats global"],
                Confidence = 0.9,
            },
            Selected = RuleStub(12),
            Ignored = [RuleStub(8)],
        };

        var notice = ActivityNoticeFactory.ForConflictResolved([resolved], "cli")!;
        Assert.Equal(ActivityType.ConflictResolved, notice.Type);
        Assert.Contains("resolved 1 rule conflict", notice.Summary, StringComparison.Ordinal);
        var verbose = ActivityNoticeRenderer.Render(notice, NoticeLevel.Verbose)!;
        Assert.Contains("chose #12 over #8", verbose, StringComparison.Ordinal);
    }

    // ---- Factory: mining and lifecycle (O, P) and no-op (U) -------------------

    [Fact] // O + U
    public void MiningNotice_OnlyWhenSomethingFound()
    {
        var empty = new MiningResult { Suggested = [], Created = 0, Updated = 0, SuppressedByRule = 0, SuppressedByRejection = 0 };
        Assert.Null(ActivityNoticeFactory.ForLessonsMined(empty, "cli"));

        var found = new MiningResult
        {
            Suggested = [new LessonCandidate { Id = 1, Title = "Validate scope first", OccurrenceCount = 3 }],
            Created = 1,
            Updated = 0,
            SuppressedByRule = 0,
            SuppressedByRejection = 0,
        };
        var notice = ActivityNoticeFactory.ForLessonsMined(found, "cli")!;
        Assert.Contains("mined 1 lesson candidate", notice.Summary, StringComparison.Ordinal);
    }

    [Fact] // P
    public void LifecycleNotice_OnlyWhenRecommendationsExist()
    {
        Assert.Null(ActivityNoticeFactory.ForLifecycle([], "cli"));

        var recs = new List<RuleLifecycleRecommendation>
        {
            new() { Id = 1, RuleId = 5, RecommendationType = RecommendationType.Promote, Confidence = 0.9 },
            new() { Id = 2, RuleId = 6, RecommendationType = RecommendationType.Archive, Confidence = 0.7 },
        };
        var notice = ActivityNoticeFactory.ForLifecycle(recs, "cli")!;
        Assert.Contains("suggested 2 lifecycle actions", notice.Summary, StringComparison.Ordinal);
        var verbose = ActivityNoticeRenderer.Render(notice, NoticeLevel.Verbose)!;
        Assert.Contains("Promote: 1", verbose, StringComparison.Ordinal);
        Assert.Contains("Archive: 1", verbose, StringComparison.Ordinal);
    }

    // ---- Recorder: persistence & dedup (V) ------------------------------------

    [Fact] // V
    public async Task DuplicateOperationHash_DoesNotCreateDuplicateRecords()
    {
        await using var db = new TestDatabase();
        await Init(db);

        await using var scope = db.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IActivityRecorder>();
        var notice = new ActivityNotice
        {
            Type = ActivityType.TurnFinalized,
            Summary = "finalized turn — captured 1.",
            OperationHash = "turn:42",
        };

        await recorder.RecordAsync(notice);
        await recorder.RecordAsync(notice);

        var all = await recorder.ListAsync(100);
        Assert.Single(all);
    }

    // ---- activity last / list (R, S, T, X, AD) --------------------------------

    [Fact] // X
    public async Task ActivityList_EmptyDb_ShowsFriendlyEmptyState()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["activity", "list"], db.Services, output);

        Assert.Equal(0, code);
        Assert.Contains("no activity recorded yet", output.ToString(), StringComparison.Ordinal);
    }

    [Fact] // R
    public async Task ActivityLast_ReturnsLatest()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, ActivityType.ContextFetched, "fetched 2 relevant rules.", at: 1);
        await Seed(db, ActivityType.RuleCaptured, "captured 1 new rule.", at: 2);

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(["activity", "last"], db.Services, output);

        Assert.Equal(0, code);
        Assert.Contains($"{Badge} captured 1 new rule.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact] // S
    public async Task ActivityList_NewestFirst()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, ActivityType.ContextFetched, "fetched.", at: 1);
        await Seed(db, ActivityType.RuleCaptured, "captured.", at: 2);
        await Seed(db, ActivityType.CandidateSkipped, "skipped.", at: 3);

        var output = new StringWriter();
        await CommandRouter.RunAsync(["activity", "list", "--limit", "5"], db.Services, output);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("skipped.", lines[0]);
        Assert.Contains("captured.", lines[1]);
        Assert.Contains("fetched.", lines[2]);
    }

    [Fact] // T + AD
    public async Task ActivityListJson_IsValidDeterministicAndPlain()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, ActivityType.ContextFetched, "fetched 3 relevant rules.", at: 1, ruleIds: "12,18,24");
        await Seed(db, ActivityType.RuleCaptured, "captured 1 new rule.", at: 2, ruleIds: "30");

        var first = new StringWriter();
        await CommandRouter.RunAsync(["activity", "list", "--json"], db.Services, first);
        var second = new StringWriter();
        await CommandRouter.RunAsync(["activity", "list", "--json"], db.Services, second);

        // Deterministic.
        Assert.Equal(first.ToString(), second.ToString());

        using var doc = JsonDocument.Parse(first.ToString());
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(2, root.GetArrayLength());

        var newest = root[0];
        Assert.Equal("captured 1 new rule.", newest.GetProperty("summary").GetString());
        Assert.Equal("RuleCaptured", newest.GetProperty("type").GetString());

        // AD: plain fields carry no Markdown; styling lives only in renderedNotice.
        Assert.DoesNotContain("🧠", newest.GetProperty("summary").GetString());
        Assert.DoesNotContain("**", newest.GetProperty("summary").GetString());
        Assert.Contains("🧠", newest.GetProperty("renderedNotice").GetString());
    }

    // ---- inject-context CLI notice & machine mode (I, J) ----------------------

    [Fact] // I
    public async Task InjectContextCli_EmitsFetchedRulesNotice()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedRule(db);

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(
            ["inject-context", "Write Moq unit tests for OrderService"], db.Services, output);

        Assert.Equal(0, code);
        Assert.Contains($"{Badge} fetched", output.ToString(), StringComparison.Ordinal);
    }

    [Fact] // J
    public async Task InjectContextCli_NoNotice_SuppressesHumanNotice()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedRule(db);

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(
            ["inject-context", "Write Moq unit tests for OrderService", "--no-notice"], db.Services, output);

        Assert.Equal(0, code);
        Assert.DoesNotContain(Badge, output.ToString(), StringComparison.Ordinal);
        // The rules themselves are still produced for machine/context use.
        Assert.Contains("Source rule IDs", output.ToString(), StringComparison.Ordinal);

        // The activity is still persisted even though the notice was suppressed.
        await using var scope = db.CreateScope();
        var last = await scope.ServiceProvider.GetRequiredService<IActivityRecorder>().GetLastAsync();
        Assert.NotNull(last);
        Assert.Equal(ActivityType.ContextFetched, last!.ActivityType);
    }

    [Fact] // U: a no-op inject (no rules) emits no notice and records no activity.
    public async Task InjectContext_NoRules_NoNoticeNoActivity()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var output = new StringWriter();
        await CommandRouter.RunAsync(["inject-context", "Write Moq unit tests"], db.Services, output);

        Assert.DoesNotContain(Badge, output.ToString(), StringComparison.Ordinal);
        await using var scope = db.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IActivityRecorder>().GetLastAsync());
    }

    // ---- feedback add CLI capture notice (Q) ----------------------------------

    [Fact] // Q
    public async Task FeedbackAddCli_EmitsCaptureNotice()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var output = new StringWriter();
        var code = await CommandRouter.RunAsync(
            ["feedback", "add", "--task", "writing tests", "--feedback",
             "Validators must enforce authorization and scope before entity-specific validation messages."],
            db.Services, output);

        Assert.Equal(0, code);
        Assert.Contains(Badge, output.ToString(), StringComparison.Ordinal);

        await using var scope = db.CreateScope();
        var last = await scope.ServiceProvider.GetRequiredService<IActivityRecorder>().GetLastAsync();
        Assert.NotNull(last);
        Assert.Contains(last!.ActivityType, new[] { ActivityType.RuleCaptured, ActivityType.RuleSuggested });
    }

    // ---- finalize-turn capture notice (K) -------------------------------------

    [Fact] // K
    public async Task FinalizeTurnCli_EmitsCapturedNotice()
    {
        await using var db = new TestDatabase();
        await Init(db);

        var payload = $$"""{"prompt": "We do not mock DbContext directly.", "cwd": "/repo/project"}""";
        var originalIn = Console.In;
        var output = new StringWriter();
        try
        {
            Console.SetIn(new StringReader(payload));
            var code = await CommandRouter.RunAsync(["finalize-turn"], db.Services, output);
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        Assert.Contains(Badge, output.ToString(), StringComparison.Ordinal);

        await using var scope = db.CreateScope();
        var last = await scope.ServiceProvider.GetRequiredService<IActivityRecorder>().GetLastAsync();
        Assert.NotNull(last);
        Assert.Equal(ActivityType.TurnFinalized, last!.ActivityType);
    }

    // ---- hook compactness & token safety (F, G, H, W, AC) ---------------------

    [Fact] // W: malformed hook input creates no activity and emits nothing.
    public async Task MalformedHookInput_NoActivityNoOutput()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedRule(db);

        var output = await RunHook(db, "{ this is not valid json ");

        Assert.Equal(string.Empty, output);
        await using var scope = db.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IActivityRecorder>().GetLastAsync());
    }

    [Fact] // F + G + H
    public async Task Hook_NoticeIsCompact_EvenWhenActivityLevelIsVerbose()
    {
        using var repo = new TempRepo();
        await using var db = new TestDatabase(o =>
        {
            o.ActivityNoticeLevel = "Verbose"; // human level is loud …
            o.HookNoticeLevel = "Normal";      // … but the hook stays compact.
        });
        await Init(db);
        await SeedRule(db);

        var output = await RunHook(db, Payload("Write Moq unit tests for OrderService", repo.Path));

        // F: a compact one-line notice is present.
        Assert.Contains($"{Badge} fetched", output, StringComparison.Ordinal);

        // G + H: the notice line itself carries no detail bullets and no rule text.
        var noticeLine = output.Split('\n').First(l => l.Contains(Badge, StringComparison.Ordinal));
        Assert.DoesNotContain("It.IsAny", noticeLine, StringComparison.Ordinal);
        Assert.DoesNotContain("- #", noticeLine, StringComparison.Ordinal);
        // The badge appears exactly once (no per-rule notice spam).
        Assert.Equal(1, CountOccurrences(output, Badge));
    }

    [Fact] // AC: notices never meaningfully grow the injected context.
    public async Task Hook_TokenSafety_NoticeAddsOnlyCompactLine()
    {
        using var repo = new TempRepo();
        const string prompt = "Write unit tests and validators for the OrderService repository";

        await using var withDb = new TestDatabase(o => o.HookNoticeLevel = "Normal");
        await Init(withDb);
        await SeedRichRules(withDb);
        var withNotice = await RunHook(withDb, Payload(prompt, repo.Path));

        await using var withoutDb = new TestDatabase(o => o.HookNoticeLevel = "Silent");
        await Init(withoutDb);
        await SeedRichRules(withoutDb);
        var withoutNotice = await RunHook(withoutDb, Payload(prompt, repo.Path));

        // Both inject the same rule block.
        Assert.Contains("## AgentRecall Technical Context", withNotice, StringComparison.Ordinal);
        Assert.DoesNotContain(Badge, withoutNotice, StringComparison.Ordinal);

        // The hard guarantee: the notice can only ever add a single compact line, so it
        // cannot bloat the injected context no matter how large the rule block is.
        var delta = withNotice.Length - withoutNotice.Length;
        Assert.True(delta > 0 && delta < 80, $"notice added {delta} chars (must be one compact line)");

        // And for a realistically-sized rule block, that line stays well under the
        // suggested 15% budget.
        Assert.True(
            withNotice.Length <= withoutNotice.Length * 1.15,
            $"with={withNotice.Length} without={withoutNotice.Length}");
    }

    // ---- Isolation (AB) -------------------------------------------------------

    [Fact] // AB
    public async Task TestDatabase_UsesIsolatedTempDir_NotHomeAgentRecall()
    {
        await using var db = new TestDatabase();
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentrecall");
        Assert.StartsWith(Path.GetTempPath(), db.Options.DataDirectory, StringComparison.Ordinal);
        Assert.NotEqual(home, db.Options.DataDirectory);
    }

    // ---- README documentation (Z, AA) -----------------------------------------

    [Fact] // Z + AA
    public void Readme_DocumentsActivityNoticesAndCommands()
    {
        var readme = File.ReadAllText(FindRepoFile("README.md"));

        Assert.Contains("## Activity Notices", readme, StringComparison.Ordinal);
        Assert.Contains("ActivityNoticeLevel", readme, StringComparison.Ordinal);
        Assert.Contains("HookNoticeLevel", readme, StringComparison.Ordinal);
        Assert.Contains("agentrecall activity last", readme, StringComparison.Ordinal);
        Assert.Contains("agentrecall activity list", readme, StringComparison.Ordinal);
    }

    // ---- helpers --------------------------------------------------------------

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }

        return count;
    }

    private static string Payload(string prompt, string cwd) =>
        $$"""{"prompt": {{JsonSerializer.Serialize(prompt)}}, "cwd": {{JsonSerializer.Serialize(cwd)}}}""";

    private static async Task<string> RunHook(TestDatabase db, string payload)
    {
        var originalIn = Console.In;
        var output = new StringWriter();
        try
        {
            Console.SetIn(new StringReader(payload));
            var code = await CommandRouter.RunAsync(["hook", "user-prompt-submit"], db.Services, output);
            Assert.Equal(0, code);
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        return output.ToString();
    }

    private static async Task SeedRule(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();
        await repo.AddAsync(new RecallRule
        {
            Trigger = "writing Moq tests",
            RuleText = "Use Moq argument matchers like It.IsAny<T>() consistently across a setup.",
            Mistake = "Never mix raw values and matchers in one setup.",
            Tags = "moq,tests,testing,matchers",
            Confidence = 0.9,
            Status = RuleStatus.Promoted,
            ScopeLevel = ScopeLevel.Global,
        });
    }

    /// <summary>Seeds several substantial dev rules so the injected block is realistically sized.</summary>
    private static async Task SeedRichRules(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>();

        var rules = new (string Trigger, string Text, string Mistake, string Tags)[]
        {
            ("writing unit tests with Moq",
             "Use Moq argument matchers like It.IsAny<T>() consistently across an entire setup rather than mixing raw values and matchers in the same call.",
             "Never mix raw argument values and matchers within a single Moq setup or verification.",
             "moq,tests,testing,unit,matchers"),
            ("writing validators for a request",
             "Validators must enforce authorization and scope before emitting any entity-specific validation messages so unauthorized callers cannot probe entity state.",
             "Do not return entity-specific validation errors before the authorization and scope checks have run.",
             "validation,validators,authorization,scope,security"),
            ("guarding a feature behind a flag",
             "Feature gates must match across backend and frontend; a flag-only check on one side and a flag-plus-limit check on the other is a recurring defect class.",
             "Never let the backend and frontend feature-gate conditions drift apart.",
             "feature,gate,flag,backend,frontend,service"),
        };

        foreach (var r in rules)
        {
            await repo.AddAsync(new RecallRule
            {
                Trigger = r.Trigger,
                RuleText = r.Text,
                Mistake = r.Mistake,
                Tags = r.Tags,
                Confidence = 0.9,
                Status = RuleStatus.Promoted,
                ScopeLevel = ScopeLevel.Global,
            });
        }
    }

    private static async Task Seed(
        TestDatabase db,
        ActivityType type,
        string summary,
        int at,
        string? ruleIds = null)
    {
        await using var scope = db.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentRecallActivityRepository>();
        await repo.AddAsync(new AgentRecallActivity
        {
            ActivityType = type,
            Summary = summary,
            RuleIds = ruleIds,
            Source = "test",
            NoticeLevel = NoticeLevel.Verbose,
            // Fixed timestamps make the JSON output deterministic.
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, at, TimeSpan.Zero),
        });
    }

    private static RecallRule RuleStub(int id = 1) => new()
    {
        Id = id,
        Trigger = "trigger",
        RuleText = "rule text",
        ScopeLevel = ScopeLevel.Global,
    };

    private static CaptureDecision Decision(CaptureOutcome outcome, string reason) =>
        new(outcome, reason, 0.5, ScopeLevel.Global, null, "notice");

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
