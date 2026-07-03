using System.Text.Json;
using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Lifecycle;
using AgentRecall.Core.Outcomes;
using AgentRecall.Core.Seeds;
using AgentRecall.Core.Summary;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Covers built-in seed packs end-to-end: the catalog, the install/remove/status service,
/// seed-aware retrieval and conflict ranking, passive confidence evolution, and the CLI
/// surface. Everything runs against an isolated temp database and never touches the real
/// data directory, the network, an LLM, or embeddings.
/// </summary>
public class SeedPackTests
{
    private static async Task<TestDatabase> NewDbAsync(Action<Core.Configuration.AgentRecallOptions>? configure = null)
    {
        var db = new TestDatabase(configure);
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        return db;
    }

    private static async Task<(int Code, string Output)> RunAsync(TestDatabase db, params string[] args)
    {
        var writer = new StringWriter();
        var code = await CommandRouter.RunAsync(args, db.Services, writer);
        return (code, writer.ToString());
    }

    private static async Task<IReadOnlyList<RecallRule>> SeedRulesAsync(TestDatabase db, string pack = TidyFirstSeedPack.Name)
    {
        await using var scope = db.CreateScope();
        var rules = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync();
        return rules
            .Where(r => r.Source == RuleSource.BuiltInSeed && r.SeedPack == pack)
            .OrderBy(r => r.Id)
            .ToList();
    }

    private static async Task<SeedInstallResult> InstallAsync(TestDatabase db, bool suggested = false, bool force = false)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISeedPackService>()
            .InstallAsync(TidyFirstSeedPack.Name, new SeedInstallOptions { Suggested = suggested, Force = force });
    }

    private static async Task<T> WithScopeAsync<T>(TestDatabase db, Func<IServiceProvider, Task<T>> body)
    {
        await using var scope = db.CreateScope();
        return await body(scope.ServiceProvider);
    }

    // ---- Catalog: rules exist, are conditional, not vague, not copyrighted ----------

    [Fact] // N,O,P,Q,R. The required rules exist and are keyed stably.
    public void Catalog_ContainsTheRequiredConditionalRules()
    {
        var keys = TidyFirstSeedPack.Definition.Rules.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("guard-clauses", keys);                 // N. early return / guard clauses
        Assert.Contains("separate-tidy-from-behavior", keys);   // O. separate tidy from behaviour
        Assert.Contains("name-repeated-condition", keys);       // P. extract repeated condition
        Assert.Contains("rename-before-logic", keys);           // Q. rename before logic
        Assert.Contains("scope-the-tidy", keys);                // R. stop tidying when unrelated
        Assert.Equal(10, TidyFirstSeedPack.Definition.Rules.Count);
    }

    [Fact] // N. Every seed rule is conditional: it has a trigger, an action, and an anti-pattern.
    public void Catalog_EverySeedRuleIsConditional()
    {
        foreach (var rule in TidyFirstSeedPack.Definition.Rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Trigger), $"{rule.Key} has no trigger");
            Assert.False(string.IsNullOrWhiteSpace(rule.Action), $"{rule.Key} has no action");
            Assert.False(string.IsNullOrWhiteSpace(rule.AntiPattern), $"{rule.Key} has no anti-pattern");
            Assert.False(string.IsNullOrWhiteSpace(rule.Because), $"{rule.Key} has no reason");
        }
    }

    [Fact] // T. Seed rules are specific and triggerable, not vague slogans.
    public void Catalog_HasNoVagueSlogans()
    {
        string[] banned = ["write clean code", "refactor first", "use good names", "keep it simple"];
        foreach (var rule in TidyFirstSeedPack.Definition.Rules)
        {
            var text = $"{rule.Title} {rule.Action}".ToLowerInvariant();
            foreach (var slogan in banned)
            {
                Assert.False(text.Contains(slogan, StringComparison.Ordinal), $"{rule.Key} reads like a vague slogan: '{slogan}'");
            }
        }
    }

    [Fact] // S. No rule carries a long copyrighted-looking passage; each field is short and original.
    public void Catalog_HasNoLongPassages()
    {
        foreach (var rule in TidyFirstSeedPack.Definition.Rules)
        {
            Assert.True(rule.Action.Length <= 220, $"{rule.Key} action is suspiciously long");
            Assert.True(rule.Because.Length <= 220, $"{rule.Key} reason is suspiciously long");
            Assert.True(rule.Trigger.Length <= 220, $"{rule.Key} trigger is suspiciously long");
        }

        Assert.Contains("no book text", TidyFirstSeedPack.Definition.CopyrightNote, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Install: idempotent, metadata, statuses ------------------------------------

    [Fact] // C,J. Installing the same pack twice creates no duplicates.
    public async Task Install_IsIdempotent()
    {
        await using var db = await NewDbAsync();
        var first = await InstallAsync(db);
        var second = await InstallAsync(db);

        Assert.Equal(10, first.Added);
        Assert.Equal(0, second.Added);
        Assert.Equal(10, second.Skipped);
        Assert.Equal(10, (await SeedRulesAsync(db)).Count);
    }

    [Fact] // A,F,G,H,I. Default install is Active with correct seed metadata and confidence.
    public async Task Install_DefaultsToActive_WithSeedMetadata()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);

        var seeds = await SeedRulesAsync(db);
        var catalogKeys = TidyFirstSeedPack.Definition.Rules.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);

        Assert.All(seeds, r =>
        {
            Assert.Equal(RuleStatus.Active, r.Status);                    // A. Active by default
            Assert.Equal(RuleSource.BuiltInSeed, r.Source);               // F. Source
            Assert.Equal(TidyFirstSeedPack.Name, r.SeedPack);            // G. SeedPack
            Assert.Contains(r.SeedRuleKey, catalogKeys);                 // H. stable key
            Assert.Equal(CaptureReason.BuiltInSeed, r.CaptureReason);
            Assert.Contains("seed", r.Tags, StringComparison.Ordinal);
            Assert.InRange(r.Confidence, 0.60, 0.70);                    // I. initial confidence range
        });
    }

    [Fact] // B. --active installs Active rules with moderate confidence, still seed-sourced.
    public async Task Install_ActiveFlag_InstallsActiveRules()
    {
        await using var db = await NewDbAsync();
        await WithScopeAsync(db, sp => sp.GetRequiredService<ISeedPackService>()
            .InstallAsync(TidyFirstSeedPack.Name, new SeedInstallOptions())); // no flags = default Active

        var seeds = await SeedRulesAsync(db);
        Assert.All(seeds, r =>
        {
            Assert.Equal(RuleStatus.Active, r.Status);
            Assert.Equal(RuleSource.BuiltInSeed, r.Source);
            Assert.InRange(r.Confidence, 0.60, 0.70);
        });
    }

    [Fact] // C. --suggested installs Pending/Suggested rules for manual approval.
    public async Task Install_Suggested_InstallsPendingRules()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db, suggested: true);

        var seeds = await SeedRulesAsync(db);
        Assert.NotEmpty(seeds);
        Assert.All(seeds, r =>
        {
            Assert.Equal(RuleStatus.Pending, r.Status);
            Assert.Equal(RuleSource.BuiltInSeed, r.Source);
            Assert.InRange(r.Confidence, 0.60, 0.70);
        });
    }

    // ---- Remove: archive only, preserve user work, don't resurrect ------------------

    [Fact] // K. Removing a pack archives its seed rules and leaves non-seed rules untouched.
    public async Task Remove_ArchivesOnlyThatPacksSeedRules()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);

        var learnedId = await WithScopeAsync(db, async sp =>
        {
            var repo = sp.GetRequiredService<IRecallRuleRepository>();
            var learned = await repo.AddAsync(new RecallRule { Trigger = "learned", RuleText = "keep me", Status = RuleStatus.Active, Confidence = 0.7 });
            return learned.Id;
        });

        await WithScopeAsync(db, sp => sp.GetRequiredService<ISeedPackService>().RemoveAsync(TidyFirstSeedPack.Name));

        var seeds = await SeedRulesAsync(db);
        Assert.All(seeds, r => Assert.Equal(RuleStatus.Archived, r.Status));

        var learnedAfter = await WithScopeAsync(db, sp => sp.GetRequiredService<IRecallRuleRepository>().GetAsync(learnedId));
        Assert.Equal(RuleStatus.Active, learnedAfter!.Status);
    }

    [Fact] // L. Removing a pack preserves user-modified and promoted seed rules.
    public async Task Remove_PreservesUserModifiedAndPromotedRules()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        var seeds = await SeedRulesAsync(db);
        var edited = seeds[0];
        var promoted = seeds[1];

        await WithScopeAsync(db, async sp =>
        {
            var repo = sp.GetRequiredService<IRecallRuleRepository>();
            edited.RuleText = "My own edited guidance.";
            await repo.UpdateAsync(edited);
            promoted.Status = RuleStatus.Promoted;
            await repo.UpdateAsync(promoted);
            return true;
        });

        var result = await WithScopeAsync(db, sp => sp.GetRequiredService<ISeedPackService>().RemoveAsync(TidyFirstSeedPack.Name));
        Assert.Equal(2, result.Preserved);
        Assert.Equal(8, result.Archived);

        var after = await SeedRulesAsync(db);
        Assert.Equal("My own edited guidance.", after.Single(r => r.Id == edited.Id).RuleText);
        Assert.NotEqual(RuleStatus.Archived, after.Single(r => r.Id == edited.Id).Status);
        Assert.Equal(RuleStatus.Promoted, after.Single(r => r.Id == promoted.Id).Status);
    }

    [Fact] // M. Removed rules are not reinstalled unless forced; --force restores clean ones only.
    public async Task Remove_ThenReinstall_DoesNotResurrectWithoutForce()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        await WithScopeAsync(db, sp => sp.GetRequiredService<ISeedPackService>().RemoveAsync(TidyFirstSeedPack.Name));

        var reinstall = await InstallAsync(db);
        Assert.Equal(0, reinstall.Added);
        Assert.Equal(0, reinstall.Restored);
        Assert.All(await SeedRulesAsync(db), r => Assert.Equal(RuleStatus.Archived, r.Status));

        var forced = await InstallAsync(db, force: true);
        Assert.Equal(10, forced.Restored);
        Assert.All(await SeedRulesAsync(db), r => Assert.NotEqual(RuleStatus.Archived, r.Status));
    }

    [Fact] // Force never overwrites a user-modified archived rule.
    public async Task ForceReinstall_DoesNotOverwriteUserEdits()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        var seeds = await SeedRulesAsync(db);
        var edited = seeds[0];

        await WithScopeAsync(db, async sp =>
        {
            var repo = sp.GetRequiredService<IRecallRuleRepository>();
            edited.RuleText = "My own edited guidance.";
            edited.Status = RuleStatus.Archived; // simulate user archiving after editing
            await repo.UpdateAsync(edited);
            return true;
        });

        var forced = await InstallAsync(db, force: true);
        Assert.Contains(forced.Changes, c => c.RuleId == edited.Id && c.Outcome == SeedRuleOutcome.SkippedUserModified);

        var after = await WithScopeAsync(db, sp => sp.GetRequiredService<IRecallRuleRepository>().GetAsync(edited.Id));
        Assert.Equal("My own edited guidance.", after!.RuleText);
    }

    // ---- Retrieval & ranking --------------------------------------------------------

    [Fact] // U. Seed rules participate in retrieval.
    public async Task SeedRules_ParticipateInRetrieval()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);

        var result = await WithScopeAsync(db, sp => sp.GetRequiredService<IContextInjectionService>()
            .BuildContextAsync(new ContextRequest { Task = "flatten nested if conditionals with guard clauses" }));

        Assert.Contains(result.All, i => i.Rule.Source == RuleSource.BuiltInSeed);
    }

    [Fact] // V. A project-specific active rule outranks a conflicting seed rule.
    public async Task ProjectRule_OutranksSeedRule()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);

        var learnedId = await WithScopeAsync(db, async sp =>
        {
            var repo = sp.GetRequiredService<IRecallRuleRepository>();
            var learned = await repo.AddAsync(new RecallRule
            {
                Trigger = "flatten nested conditionals with guard clauses",
                RuleText = "Use our project helper to flatten nested conditionals.",
                Status = RuleStatus.Active,
                Confidence = 0.7,
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "myrepo",
            });
            return learned.Id;
        });

        var result = await WithScopeAsync(db, sp => sp.GetRequiredService<IContextInjectionService>()
            .BuildContextAsync(new ContextRequest
            {
                Task = "flatten nested conditionals with guard clauses",
                ScopeLevel = ScopeLevel.Repository,
                ScopeValue = "myrepo",
            }));

        // The project rule is must-follow; no seed rule is ever elevated to must-follow.
        Assert.Contains(result.MustFollow, i => i.Rule.Id == learnedId);
        Assert.DoesNotContain(result.MustFollow, i => i.Rule.Source == RuleSource.BuiltInSeed);

        // And the learned rule ranks ahead of every seed rule.
        var order = result.All.Select(i => i.Rule).ToList();
        var learnedRank = order.FindIndex(r => r.Id == learnedId);
        var firstSeedRank = order.FindIndex(r => r.Source == RuleSource.BuiltInSeed);
        Assert.True(learnedRank >= 0 && (firstSeedRank < 0 || learnedRank < firstSeedRank));
    }

    [Fact] // W. Seed injection is capped for a non-tidy task.
    public async Task SeedInjection_IsCapped_ForNonTidyTasks()
    {
        await using var db = await NewDbAsync();
        await WithScopeAsync(db, async sp =>
        {
            var repo = sp.GetRequiredService<IRecallRuleRepository>();
            for (var i = 0; i < 4; i++)
            {
                await repo.AddAsync(new RecallRule
                {
                    Trigger = "when writing database migration code",
                    RuleText = $"Handle database migration concern {i}.",
                    Tags = "database, migration",
                    Status = RuleStatus.Active,
                    Confidence = 0.65,
                    Source = RuleSource.BuiltInSeed,
                    SeedPack = "demo",
                    SeedRuleKey = $"demo-{i}",
                });
            }

            return true;
        });

        var result = await WithScopeAsync(db, sp => sp.GetRequiredService<IContextInjectionService>()
            .BuildContextAsync(new ContextRequest { Task = "database migration schema" }));

        Assert.Equal(2, result.All.Count(i => i.Rule.Source == RuleSource.BuiltInSeed));
    }

    [Fact] // W (lift). A tidy/refactor task is allowed more than the cap of seed rules.
    public async Task SeedInjection_CapLifted_ForTidyTasks()
    {
        await using var db = await NewDbAsync();
        await WithScopeAsync(db, async sp =>
        {
            var repo = sp.GetRequiredService<IRecallRuleRepository>();
            for (var i = 0; i < 4; i++)
            {
                await repo.AddAsync(new RecallRule
                {
                    Trigger = "when refactoring code for readability",
                    RuleText = $"Refactor readability concern {i}.",
                    Tags = "refactor, readability",
                    Status = RuleStatus.Active,
                    Confidence = 0.65,
                    Source = RuleSource.BuiltInSeed,
                    SeedPack = "demo",
                    SeedRuleKey = $"demo-{i}",
                });
            }

            return true;
        });

        var result = await WithScopeAsync(db, sp => sp.GetRequiredService<IContextInjectionService>()
            .BuildContextAsync(new ContextRequest { Task = "refactor this code for readability" }));

        Assert.True(result.All.Count(i => i.Rule.Source == RuleSource.BuiltInSeed) > 2);
    }

    // ---- Confidence evolution -------------------------------------------------------

    private static async Task ApplyRetrievalsAsync(TestDatabase db, int ruleId, int times)
    {
        await using var scope = db.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IRecallEventRepository>();
        for (var i = 0; i < times; i++)
        {
            await events.AddAsync(new RecallEvent { Type = RecallEventType.RuleApplied, RuleId = ruleId, Trigger = "retrieval", Details = "" });
        }
    }

    private static async Task<double> ConfidenceAsync(TestDatabase db, int ruleId) =>
        (await WithScopeAsync(db, sp => sp.GetRequiredService<IRecallRuleRepository>().GetAsync(ruleId)))!.Confidence;

    [Fact] // X. Passive reinforcement raises seed confidence after repeated uneventful use.
    public async Task PassiveReinforcement_RaisesConfidence()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        var rule = (await SeedRulesAsync(db))[0];
        var before = rule.Confidence;

        await ApplyRetrievalsAsync(db, rule.Id, 3);
        await WithScopeAsync(db, sp => sp.GetRequiredService<ISeedConfidenceService>().ReinforceAsync());

        var after = await ConfidenceAsync(db, rule.Id);
        Assert.True(after > before, $"expected {after} > {before}");
        Assert.True(after <= SeedConfidenceService.PassiveCeiling);
    }

    [Fact] // X (idempotent). Re-running reinforcement without new uses does not keep raising confidence.
    public async Task PassiveReinforcement_IsIdempotent()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        var rule = (await SeedRulesAsync(db))[0];

        await ApplyRetrievalsAsync(db, rule.Id, 3);
        await WithScopeAsync(db, sp => sp.GetRequiredService<ISeedConfidenceService>().ReinforceAsync());
        var afterFirst = await ConfidenceAsync(db, rule.Id);

        await WithScopeAsync(db, sp => sp.GetRequiredService<ISeedConfidenceService>().ReinforceAsync());
        var afterSecond = await ConfidenceAsync(db, rule.Id);

        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact] // Y. Passive reinforcement is capped and never runs confidence away.
    public async Task PassiveReinforcement_IsCapped()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        var rule = (await SeedRulesAsync(db))[0];

        await ApplyRetrievalsAsync(db, rule.Id, 100);
        await WithScopeAsync(db, sp => sp.GetRequiredService<ISeedConfidenceService>().ReinforceAsync());

        Assert.Equal(SeedConfidenceService.PassiveCeiling, await ConfidenceAsync(db, rule.Id), 3);
    }

    [Fact] // Z. Explicit acceptance raises seed confidence more than a single passive use.
    public async Task ExplicitAcceptance_RaisesMoreThanPassiveUse()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        var seeds = await SeedRulesAsync(db);
        var passiveRule = seeds[0];
        var acceptedRule = seeds[1];
        var baseline = passiveRule.Confidence;

        await ApplyRetrievalsAsync(db, passiveRule.Id, 1);
        await WithScopeAsync(db, sp => sp.GetRequiredService<ISeedConfidenceService>().ReinforceAsync());

        await WithScopeAsync(db, sp => sp.GetRequiredService<IOutcomeTrackingService>()
            .RecordAsync(new OutcomeRequest { RuleId = acceptedRule.Id, Type = OutcomeType.UserAccepted }));

        var passiveGain = await ConfidenceAsync(db, passiveRule.Id) - baseline;
        var acceptedGain = await ConfidenceAsync(db, acceptedRule.Id) - baseline;
        Assert.True(acceptedGain > passiveGain, $"accepted {acceptedGain} should beat passive {passiveGain}");
    }

    [Fact] // AA. User rejection decreases seed confidence.
    public async Task Rejection_DecreasesConfidence()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        var rule = (await SeedRulesAsync(db))[0];
        var before = rule.Confidence;

        await WithScopeAsync(db, sp => sp.GetRequiredService<IOutcomeTrackingService>()
            .RecordAsync(new OutcomeRequest { RuleId = rule.Id, Type = OutcomeType.UserRejected }));

        Assert.True(await ConfidenceAsync(db, rule.Id) < before);
    }

    [Fact] // AA (guard). A rejected seed rule is not passively reinforced afterwards.
    public async Task RejectedRule_IsNotPassivelyReinforced()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        var rule = (await SeedRulesAsync(db))[0];

        await WithScopeAsync(db, sp => sp.GetRequiredService<IOutcomeTrackingService>()
            .RecordAsync(new OutcomeRequest { RuleId = rule.Id, Type = OutcomeType.UserRejected }));
        var afterReject = await ConfidenceAsync(db, rule.Id);

        await ApplyRetrievalsAsync(db, rule.Id, 5);
        await WithScopeAsync(db, sp => sp.GetRequiredService<ISeedConfidenceService>().ReinforceAsync());

        Assert.Equal(afterReject, await ConfidenceAsync(db, rule.Id));
    }

    [Fact] // AB. Repeated rejection produces a lifecycle recommendation (archive/lower/review).
    public async Task RepeatedRejection_ProducesLifecycleRecommendation()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        var rule = (await SeedRulesAsync(db))[0];

        await WithScopeAsync(db, async sp =>
        {
            var tracker = sp.GetRequiredService<IOutcomeTrackingService>();
            await tracker.RecordAsync(new OutcomeRequest { RuleId = rule.Id, Type = OutcomeType.UserRejected, RetrievalId = "r1" });
            await tracker.RecordAsync(new OutcomeRequest { RuleId = rule.Id, Type = OutcomeType.CorrectionRepeated, RetrievalId = "r2" });
            return true;
        });

        var recs = await WithScopeAsync(db, sp => sp.GetRequiredService<IRuleLifecycleRecommendationService>()
            .SuggestAsync(new RecommendationQuery { AsOf = DateTimeOffset.UtcNow }));

        Assert.Contains(recs, r => r.RuleId == rule.Id &&
            r.RecommendationType is RecommendationType.LowerConfidence or RecommendationType.Archive or RecommendationType.Review);
    }

    [Fact] // AC. A seed rule can be promoted once repeated success lifts its confidence.
    public async Task SeedRule_CanBePromoted()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        var rule = (await SeedRulesAsync(db))[0];

        var promoted = await WithScopeAsync(db, sp => sp.GetRequiredService<IRuleLifecycleService>()
            .ReinforceAsync(rule.Id, 0.2)); // 0.65 -> 0.85 crosses the promote threshold

        Assert.Equal(RuleStatus.Promoted, promoted.Status);
        Assert.Equal(RuleSource.BuiltInSeed, promoted.Source);
    }

    // ---- Turn summary [seed] marker -------------------------------------------------

    [Fact] // AE. The Turn Summary renders a [seed] marker for a used seed rule.
    public void TurnSummary_RendersSeedMarker()
    {
        var summary = new TurnSummary
        {
            Used = [new TurnSummaryRule { Id = 7, Title = "Flatten nested conditionals", Seed = true }],
        };

        var rendered = TurnSummaryRenderer.RenderDetailed(summary);
        Assert.Contains("[seed]", rendered, StringComparison.Ordinal);
    }

    [Fact] // AE. The Turn Summary service flags a used seed rule as seed-derived.
    public async Task TurnSummaryService_FlagsUsedSeedRule()
    {
        await using var db = await NewDbAsync();
        await InstallAsync(db);
        var seed = (await SeedRulesAsync(db))[0];

        await WithScopeAsync(db, async sp =>
        {
            await sp.GetRequiredService<IAgentRecallActivityRepository>().AddAsync(new AgentRecallActivity
            {
                ActivityType = ActivityType.ContextFetched,
                TurnId = "turn-seed",
                RuleIds = seed.Id.ToString(),
                Summary = "fetched",
                Source = "test",
            });
            return true;
        });

        var summary = await WithScopeAsync(db, sp => sp.GetRequiredService<ITurnSummaryService>().BuildForTurnAsync("turn-seed"));
        Assert.Contains(summary.Used, r => r.Id == seed.Id && r.Seed);
    }

    // ---- CLI surface ----------------------------------------------------------------

    [Fact] // A. `seed list` shows tidy-first.
    public async Task Cli_SeedList_ShowsTidyFirst()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed", "list");
        Assert.Equal(0, code);
        Assert.Contains("tidy-first", output, StringComparison.Ordinal);
        Assert.Contains("not installed", output, StringComparison.Ordinal);
    }

    [Fact] // B. `seed show tidy-first` shows rule titles and default confidence.
    public async Task Cli_SeedShow_ShowsTitlesAndDefaults()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed", "show", "tidy-first");
        Assert.Equal(0, code);
        Assert.Contains("Flatten nested conditionals with guard clauses", output, StringComparison.Ordinal);
        Assert.Contains("0.65", output, StringComparison.Ordinal);
        Assert.Contains("Active", output, StringComparison.Ordinal);
    }

    [Fact] // D. Default install output says Active, not Suggested, and states seeds are overridable.
    public async Task Cli_SeedInstall_PrintsSummary()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed", "install", "tidy-first");
        Assert.Equal(0, code);
        Assert.Contains("installed seed pack `tidy-first`", output, StringComparison.Ordinal);
        Assert.Contains("10 seed rules installed as Active with moderate confidence.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("added as Suggested", output, StringComparison.Ordinal);
        Assert.Contains("Project-specific rules and explicit user corrections override them.", output, StringComparison.Ordinal);
    }

    [Fact] // C,D. --suggested installs Pending rules and says so.
    public async Task Cli_SeedInstall_Suggested_PrintsSuggested()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed", "install", "tidy-first", "--suggested");
        Assert.Equal(0, code);
        Assert.Contains("installed as Suggested", output, StringComparison.Ordinal);
        Assert.All(await SeedRulesAsync(db), r => Assert.Equal(RuleStatus.Pending, r.Status));
    }

    [Fact] // AD. Installing a pack emits an activity notice.
    public async Task Cli_SeedInstall_EmitsActivityNotice()
    {
        await using var db = await NewDbAsync();
        await RunAsync(db, "seed", "install", "tidy-first");

        var (code, output) = await RunAsync(db, "activity", "last");
        Assert.Equal(0, code);
        Assert.Contains("seed pack `tidy-first`", output, StringComparison.Ordinal);
    }

    [Fact] // Unknown pack fails cleanly.
    public async Task Cli_SeedInstall_UnknownPack_Fails()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "seed", "install", "does-not-exist");
        Assert.Equal(1, code);
        Assert.Contains("Unknown seed pack", output, StringComparison.Ordinal);
    }

    [Fact] // AH. JSON output is valid for the seed commands.
    public async Task Cli_SeedCommands_EmitValidJson()
    {
        await using var db = await NewDbAsync();
        await RunAsync(db, "seed", "install", "tidy-first");

        foreach (var args in new[]
        {
            new[] { "seed", "list", "--json" },
            new[] { "seed", "show", "tidy-first", "--json" },
            new[] { "seed", "status", "--json" },
        })
        {
            var (code, output) = await RunAsync(db, args);
            Assert.Equal(0, code);
            var doc = JsonDocument.Parse(output); // throws if invalid
            Assert.NotNull(doc);
        }

        var (installCode, installJson) = await RunAsync(db, "seed", "install", "tidy-first", "--json");
        Assert.Equal(0, installCode);
        using var installed = JsonDocument.Parse(installJson);
        Assert.Equal("tidy-first", installed.RootElement.GetProperty("pack").GetString());
    }

    [Fact] // seed status reports installed counts.
    public async Task Cli_SeedStatus_ReportsCounts()
    {
        await using var db = await NewDbAsync();
        await RunAsync(db, "seed", "install", "tidy-first", "--active");

        var (code, output) = await RunAsync(db, "seed", "status");
        Assert.Equal(0, code);
        Assert.Contains("installed", output, StringComparison.Ordinal);
        Assert.Contains("Active: 10", output, StringComparison.Ordinal);
    }

    // ---- Docs -----------------------------------------------------------------------

    [Fact] // E,AF. README documents seed packs, the Active default, and the --suggested mode.
    public void Readme_DocumentsSeedPacks()
    {
        var readme = File.ReadAllText(FindRepoFile("README.md"));
        Assert.Contains("## Seed Packs", readme, StringComparison.Ordinal);
        Assert.Contains("agentrecall seed install tidy-first", readme, StringComparison.Ordinal);
        Assert.Contains("agentrecall seed remove tidy-first", readme, StringComparison.Ordinal);
        Assert.Contains("Active by default", readme, StringComparison.Ordinal);
        Assert.Contains("--suggested", readme, StringComparison.Ordinal);
    }

    [Fact] // F,AG. The scaffolded CLAUDE.md says seeds are active starter guidance, not absolute truth.
    public void Scaffold_ExplainsSeedRulesAreActiveButNotAbsolute()
    {
        var scaffold = File.ReadAllText(FindRepoFile(Path.Combine("src", "AgentRecall.Cli", "Devcontainer", "DevcontainerScaffolder.cs")));
        Assert.Contains("Seed rules", scaffold, StringComparison.Ordinal);
        Assert.Contains("active starter guidance", scaffold, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not project-specific truth", scaffold, StringComparison.Ordinal);
        Assert.Contains("do not treat a seed rule as absolute", scaffold, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate {relativePath} above {AppContext.BaseDirectory}.");
    }
}
