using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Cli.Devcontainer;
using AgentRecall.Cli.Hooks;
using AgentRecall.Cli.Mcp.Tools;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Concepts;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Covers putting knowledge in its place: rules anchored to paths and symbols, domain concepts,
/// and recall on reading a file.
/// </summary>
public class PlacementTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<int> Seed(
        TestDatabase db,
        string ruleText,
        string anchors = "",
        string tags = "",
        string trigger = "",
        ScopeLevel scopeLevel = ScopeLevel.Global,
        string scopeValue = "")
    {
        await using var scope = db.CreateScope();
        var rule = await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().AddAsync(new RecallRule
        {
            Trigger = trigger, RuleText = ruleText, Mistake = "", TechnicalContext = "", Tags = tags,
            Confidence = 0.9, Status = RuleStatus.Active, Anchors = anchors,
            ScopeLevel = scopeLevel, ScopeValue = scopeValue,
        });
        return rule.Id;
    }

    private static async Task<ContextInjectionResult> Build(TestDatabase db, ContextRequest request)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IContextInjectionService>().BuildContextAsync(request);
    }

    private static async Task<RecallRule> Rule(TestDatabase db, int id)
    {
        await using var scope = db.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().GetAsync(id))!;
    }

    private static async Task<(int Code, string Output)> RunAsync(TestDatabase db, params string[] args)
    {
        var writer = new StringWriter();
        var code = await CommandRouter.RunAsync(args, db.Services, writer);
        return (code, writer.ToString());
    }

    private static string ToolPayload(string tool, string filePath, string? content = null)
    {
        var input = new JsonObject { ["file_path"] = filePath };
        if (content is not null)
        {
            input["content"] = content;
        }

        return new JsonObject { ["tool_name"] = tool, ["cwd"] = "/tmp/project", ["tool_input"] = input }.ToJsonString();
    }

    // ---- Anchor syntax and matching -----------------------------------------

    [Theory]
    [InlineData("tests/**", "/Users/me/repo/tests/Unit/OrderTests.cs", true)]
    [InlineData("tests/**", "/Users/me/repo/src/Orders/Order.cs", false)]
    [InlineData("src/Billing", "/Users/me/repo/src/Billing/Invoices/Invoice.cs", true)]
    [InlineData("src/Billing", "/Users/me/repo/src/BillingOld/Invoice.cs", false)]
    [InlineData("**/*Controller.cs", "/repo/src/Api/OrdersController.cs", true)]
    [InlineData("**/*Controller.cs", "/repo/src/Api/OrdersService.cs", false)]
    [InlineData("./Tests/", "C:\\repo\\tests\\A.cs", true)]
    public void PathMatches_FromAnyDirectoryOnASegmentBoundary(string pattern, string file, bool expected) =>
        Assert.Equal(expected, RuleAnchors.PathMatches(pattern, file));

    [Theory]
    [InlineData("path:tests/**", true)]
    [InlineData("symbol:OrderService", true)]
    [InlineData("symbol:Order Service", false)]
    [InlineData("symbol:1Order", false)]
    [InlineData("folder:tests", false)]
    [InlineData("tests/**", false)]
    [InlineData("path:", false)]
    public void TryParse_AcceptsOnlyWellFormedAnchors(string text, bool expected) =>
        Assert.Equal(expected, RuleAnchors.TryParse(text, out _));

    [Fact]
    public void Format_RoundTripsWithoutRepeats()
    {
        var anchors = RuleAnchors.Parse("path:tests/**\nsymbol:OrderService\npath:tests/**\nnonsense");

        Assert.Equal("path:tests/**\nsymbol:OrderService", RuleAnchors.Format(anchors));
    }

    [Fact]
    public void DeclaredSymbols_FindsTypesAcrossLanguages()
    {
        var symbols = RuleAnchors.DeclaredSymbols("""
            public sealed class OrderService : IOrderService { }
            interface IOrderService {}
            def charge(order): pass
            export function refund() {}
            """);

        Assert.Equal(["OrderService", "IOrderService", "charge", "refund"], symbols);
    }

    // ---- Anchored recall ----------------------------------------------------

    [Fact]
    public async Task PathAnchoredRule_IsDeliveredInItsPlace_EvenWithoutSharedWords()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Build fixtures with the builders in TestData, never by hand.", anchors: "path:tests/**");

        var result = await Build(db, new ContextRequest
        {
            Task = "Editing OrderTests.cs",
            FileNames = ["/repo/tests/Unit/OrderTests.cs"],
        });

        var injected = Assert.Single(result.All);
        Assert.Equal(RuleImportance.MustFollow, injected.Importance);
        Assert.Contains("anchored to path:tests/**", injected.Explanation);
    }

    [Fact]
    public async Task AnchoredRule_IsHeldBack_WhenTheWorkIsElsewhere()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Build order fixtures with the builders.", anchors: "path:tests/**", tags: "order,fixtures");

        var result = await Build(db, new ContextRequest
        {
            Task = "Editing Order.cs order fixtures",
            FileNames = ["/repo/src/Orders/Order.cs"],
        });

        Assert.Empty(result.All);
    }

    [Fact]
    public async Task AnchoredRule_CompetesOnKeywords_WhenNoFileIsKnown()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Build order fixtures with the builders.", anchors: "path:tests/**", tags: "order,fixtures");

        var result = await Build(db, new ContextRequest { Task = "write order fixtures" });

        Assert.Single(result.All);
    }

    [Fact]
    public async Task SymbolAnchor_MatchesTheDeclaringFile_AndATaskThatNamesIt()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Keep OrderService methods public virtual.", anchors: "symbol:OrderService");

        var byEntity = await Build(db, new ContextRequest
        {
            Task = "Editing Orders.cs",
            FileNames = ["/repo/src/Orders.cs"],
            ChangedEntities = ["OrderService"],
        });
        var byPrompt = await Build(db, new ContextRequest { Task = "the ticket says orderservice must refund twice" });

        Assert.Single(byEntity.All);
        Assert.Single(byPrompt.All);
    }

    [Fact]
    public async Task RepositoryAnchor_DoesNotFireInAnotherRepository()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Use the shared fixture builders.", anchors: "path:tests/**",
            scopeLevel: ScopeLevel.Repository, scopeValue: "billing");

        var result = await Build(db, new ContextRequest
        {
            Task = "Editing A.cs",
            FileNames = ["/repo/tests/A.cs"],
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "storefront",
        });

        Assert.Empty(result.All);
    }

    [Fact]
    public async Task AnchoredOnly_AdmitsNothingOnKeywordsAlone()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Order totals are rounded half to even.", tags: "order,orders,reading");
        var anchored = await Seed(db, "Read the invariants in the header before changing OrderService.", anchors: "symbol:OrderService");

        var result = await Build(db, new ContextRequest
        {
            Task = "Reading OrderService.cs order orders",
            FileNames = ["/repo/src/OrderService.cs"],
            AnchoredOnly = true,
        });

        Assert.Equal(anchored, Assert.Single(result.All).Rule.Id);
    }

    // ---- Reading a file -----------------------------------------------------

    [Fact]
    public async Task ReadingAFile_DeliversTheRulesAnchoredToTheTypesItDeclares()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Charge only through the PaymentGateway, never the SDK directly.", anchors: "symbol:Checkout");
        await Seed(db, "Services expose public virtual methods.", tags: "service,services,reading");

        var file = Path.Combine(Path.GetTempPath(), $"agentrecall-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(file, "namespace Shop;\npublic class Checkout { }\n");
        try
        {
            var output = await PreToolUseHook.RunAsync(ToolPayload("Read", file), db.Services, new StringWriter());

            Assert.Contains("PaymentGateway", output.AdditionalContext);
            Assert.DoesNotContain("public virtual", output.AdditionalContext);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ReadingAFile_WithNothingAnchored_InjectsNothing()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Services expose public virtual methods.", tags: "service,services");

        var output = await PreToolUseHook.RunAsync(
            ToolPayload("Read", "/tmp/project/OrderService.cs"), db.Services, new StringWriter());

        Assert.True(output.IsEmpty);
    }

    [Fact]
    public async Task Scaffolder_WidensAnOlderPreToolUseMatcher_InPlace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentrecall-{Guid.NewGuid():N}");
        var settings = Path.Combine(root, DevcontainerScaffolder.ClaudeSettingsRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        await File.WriteAllTextAsync(settings, new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["matcher"] = "Edit|Write|MultiEdit",
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "command", ["command"] = DevcontainerScaffolder.PreToolUseHookCommand },
                        },
                    },
                },
            },
        }.ToJsonString());

        try
        {
            DevcontainerScaffolder.Init(root);

            var groups = JsonNode.Parse(await File.ReadAllTextAsync(settings))!["hooks"]!["PreToolUse"]!.AsArray();
            var group = Assert.Single(groups);
            Assert.Equal("Read|Edit|Write|MultiEdit", group!["matcher"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- Capture ------------------------------------------------------------

    [Fact]
    public async Task JudgedCapture_StoresTheAnchorsTheJudgeNamed()
    {
        await using var db = new TestDatabase();
        await Init(db);

        JsonNode result;
        await using (var scope = db.CreateScope())
        {
            result = await new SubmitCaptureJudgmentTool().InvokeAsync(new JsonObject
            {
                ["decision"] = "Capture",
                ["memory_type"] = "RepositoryConvention",
                ["capture_reason"] = "UserCorrection",
                ["confidence"] = 0.9,
                ["prompt"] = "in the tests folder always build fixtures with the builders",
                ["assistant_response"] = "Switched the fixtures to the builders.",
                ["normalized_rule"] = new JsonObject
                {
                    ["title"] = "Build test fixtures with the builders",
                    ["condition"] = "when creating fixtures in tests",
                    ["action"] = "use the TestData builders instead of constructing entities by hand",
                    ["because"] = "hand-built fixtures drift from the real invariants",
                    ["scope"] = "repository",
                    ["anchors"] = new JsonObject
                    {
                        ["paths"] = new JsonArray { "tests/**", "   " },
                        ["symbols"] = new JsonArray { "OrderBuilder", "not a symbol" },
                    },
                },
            }, scope.ServiceProvider, default);
        }

        Assert.True(result["submitted"]!.GetValue<bool>());
        await using var read = db.CreateScope();
        var rule = Assert.Single(await read.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync());
        Assert.Equal("path:tests/**\nsymbol:OrderBuilder", rule.Anchors);
    }

    [Fact]
    public void ParseVerdict_AcceptsAnchorsAsAStringArray()
    {
        var verdict = TurnPayload.ParseVerdict(JsonNode.Parse("""
        {
          "decision": "Capture", "memory_type": "RepositoryConvention", "capture_reason": "UserCorrection",
          "normalized_rule": { "condition": "when x", "action": "do y", "anchors": ["path:src/Billing", "symbol:Invoice"] }
        }
        """), TextWriter.Null);

        Assert.Equal(
            [new RuleAnchor(AnchorKind.Path, "src/Billing"), new RuleAnchor(AnchorKind.Symbol, "Invoice")],
            verdict!.NormalizedRule!.Anchors);
    }

    // ---- Concepts -----------------------------------------------------------

    [Fact]
    public async Task Concept_SavedWithAPersianName_IsDeliveredForAPersianPrompt()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using (var scope = db.CreateScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<KnowledgePlacement>().SaveConceptAsync(new ConceptInput
            {
                Term = "event",
                Definition = "A booking change published to subscribers, only after the transaction commits.",
                Aliases = ["ایونت", "domain event"],
            });

            // Only the name in another script goes to the glossary; an English alias stays a tag.
            var mapped = Assert.Single(saved.GlossaryTerms);
            Assert.Equal("event", mapped.Canonical);
        }

        var output = await UserPromptSubmitHook.RunAsync(
            new JsonObject { ["prompt"] = "ایونت رو درست کن", ["cwd"] = "/tmp/project" }.ToJsonString(),
            db.Services, new StringWriter());

        Assert.Contains("Domain Concepts:", output);
        Assert.Contains("event: A booking change", output);
        Assert.DoesNotContain("Must Follow:", output);
    }

    [Fact]
    public async Task Concept_IsNeverAWarning_AndDoesNotConflictWithRules()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await Seed(db, "Publish an event only after commit.", tags: "event", trigger: "publishing an event");
        await using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<KnowledgePlacement>().SaveConceptAsync(new ConceptInput
            {
                Term = "event",
                Definition = "Never a command: an event reports what already happened.",
            });
        }

        var result = await Build(db, new ContextRequest { Task = "publish the event" });

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.All.Count());
        var formatted = HookContextFormatter.Format(result);
        Assert.Contains("Domain Concepts:", formatted);
        Assert.Contains("event: Never a command", formatted);
    }

    [Fact]
    public async Task Concept_ResavedUnderTheSameTerm_IsUpdatedNotDuplicated()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await using var scope = db.CreateScope();
        var placement = scope.ServiceProvider.GetRequiredService<KnowledgePlacement>();

        var first = await placement.SaveConceptAsync(new ConceptInput { Term = "Venue", Definition = "A bookable place.", Repository = "app" });
        var second = await placement.SaveConceptAsync(new ConceptInput
        {
            Term = "venue",
            Definition = "A bookable place with its own opening hours.",
            Anchors = [new RuleAnchor(AnchorKind.Symbol, "Venue")],
            Repository = "app",
        });

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Concept.Id, second.Concept.Id);
        Assert.Equal("A bookable place with its own opening hours.", second.Concept.RuleText);
        Assert.Equal("symbol:Venue", second.Concept.Anchors);
    }

    // ---- CLI and MCP --------------------------------------------------------

    [Fact]
    public async Task Cli_RulesAnchor_AddsAndRemoves()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await Seed(db, "Use the builders.");

        var (addCode, addOut) = await RunAsync(db, "rules", "anchor", id.ToString(), "add", "path:tests/**", "symbol:OrderBuilder");
        Assert.Equal(0, addCode);
        Assert.Contains("path:tests/**, symbol:OrderBuilder", addOut);

        var (badCode, badOut) = await RunAsync(db, "rules", "anchor", id.ToString(), "add", "tests/**");
        Assert.Equal(1, badCode);
        Assert.Contains("Not an anchor", badOut);

        var (removeCode, _) = await RunAsync(db, "rules", "anchor", id.ToString(), "remove", "path:tests/**");
        Assert.Equal(0, removeCode);
        Assert.Equal("symbol:OrderBuilder", (await Rule(db, id)).Anchors);

        var (_, showOut) = await RunAsync(db, "rules", "show", id.ToString());
        Assert.Contains("Anchored to:       symbol:OrderBuilder", showOut);
    }

    [Fact]
    public async Task Cli_ConceptAddAndList()
    {
        await using var db = new TestDatabase();

        var (addCode, addOut) = await RunAsync(db, "concept", "add", "event", "A change published after commit.",
            "--aliases", "ایونت", "--paths", "src/Events", "--global");
        Assert.Equal(0, addCode);
        Assert.Contains("Saved concept:", addOut);
        Assert.Contains("Glossary:", addOut);

        var (_, listOut) = await RunAsync(db, "concept", "list");
        Assert.Contains("event", listOut);
        Assert.Contains("Anchored to: path:src/Events", listOut);
        Assert.Contains("Scope: global", listOut);

        var (_, glossaryOut) = await RunAsync(db, "glossary", "list");
        Assert.Contains("ایونت", glossaryOut);

        var (usageCode, _) = await RunAsync(db, "concept", "add", "event");
        Assert.Equal(1, usageCode);
    }

    [Fact]
    public async Task Mcp_SaveConceptAndAnchorRule()
    {
        await using var db = new TestDatabase();
        await Init(db);
        var id = await Seed(db, "Use the builders.");

        await using var scope = db.CreateScope();
        var concept = await new SaveConceptTool().InvokeAsync(new JsonObject
        {
            ["term"] = "invoice",
            ["definition"] = "A bill issued once a booking is confirmed.",
            ["aliases"] = new JsonArray { "فاکتور" },
            ["symbols"] = new JsonArray { "Invoice", "bad symbol" },
            ["global"] = true,
        }, scope.ServiceProvider, default);

        Assert.True(concept["saved"]!.GetValue<bool>());
        Assert.Equal("symbol:Invoice", Assert.Single(concept["anchors"]!.AsArray())!.GetValue<string>());
        Assert.Equal("symbol:bad symbol", Assert.Single(concept["rejected_anchors"]!.AsArray())!.GetValue<string>());

        var placed = await new AnchorRuleTool().InvokeAsync(new JsonObject
        {
            ["rule_id"] = id,
            ["paths"] = new JsonArray { "tests/**" },
        }, scope.ServiceProvider, default);
        Assert.True(placed["placed"]!.GetValue<bool>());

        var missing = await new AnchorRuleTool().InvokeAsync(new JsonObject { ["rule_id"] = 9999 }, scope.ServiceProvider, default);
        Assert.False(missing["placed"]!.GetValue<bool>());
    }

    // ---- Repository scope ---------------------------------------------------

    [Fact]
    public void RepositoryScope_OfALinkedWorktree_IsTheMainCheckout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentrecall-{Guid.NewGuid():N}");
        var main = Path.Combine(root, "shop");
        var worktree = Path.Combine(root, "shop-feature");
        Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "shop-feature"));
        Directory.CreateDirectory(Path.Combine(worktree, "src"));
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine(main, ".git", "worktrees", "shop-feature")}\n");
        try
        {
            Assert.Equal("shop", TurnPayload.RepositoryScopeName(Path.Combine(worktree, "src")));
            Assert.Equal("shop", TurnPayload.RepositoryScopeName(main));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RepositoryScope_OfASubmodule_IsTheSubmodule()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentrecall-{Guid.NewGuid():N}");
        var module = Path.Combine(root, "vendor-lib");
        Directory.CreateDirectory(module);
        File.WriteAllText(Path.Combine(module, ".git"), "gitdir: ../.git/modules/vendor-lib\n");
        try
        {
            Assert.Equal("vendor-lib", TurnPayload.RepositoryScopeName(module));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
