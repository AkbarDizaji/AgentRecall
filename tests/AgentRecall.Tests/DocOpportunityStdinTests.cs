using System.Text.Json.Nodes;
using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.DocOpportunity;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Document-opportunity integration on the finalize-turn (Stop-hook) path, which reads the
/// payload from <see cref="Console.In"/>, and the <c>document write</c> command, which reads
/// the document body the same way. These redirect the process-global stdin, so they live in
/// the serialized ConsoleStdin collection. Asserts the judge runs at end-of-turn, never blocks,
/// prints the compact summary only on the human path, keeps the full summary out of the
/// model-visible hook output, and that <c>document write</c> auto-suffixes rather than
/// overwriting on a naming collision.
/// </summary>
[Collection("ConsoleStdin")]
public class DocOpportunityStdinTests
{
    private static async Task<TestDatabase> NewDbAsync(Action<Core.Configuration.AgentRecallOptions>? configure = null)
    {
        var db = new TestDatabase(configure);
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        return db;
    }

    private static async Task<(int Code, string Output)> RunWithStdinAsync(
        TestDatabase db, string stdin, params string[] args)
    {
        var originalIn = Console.In;
        var writer = new StringWriter();
        try
        {
            Console.SetIn(new StringReader(stdin));
            var code = await CommandRouter.RunAsync(args, db.Services, writer);
            return (code, writer.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    private static string Payload(string prompt, string response, object? docOpportunity = null)
    {
        var cwd = Path.Combine(Path.GetTempPath(), "doc-opportunity-turn");
        var obj = new JsonObject
        {
            ["cwd"] = cwd,
            ["source"] = "stop_hook",
            ["prompt"] = prompt,
            ["assistant_response"] = response,
        };

        if (docOpportunity is not null)
        {
            obj["doc_opportunity"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(docOpportunity));
        }

        return obj.ToJsonString();
    }

    private static readonly object OfferPayload = new
    {
        decision = "Offer",
        document_type = "Rfc",
        confidence = 0.8,
        suggested_title = "Adopt the shared caching layer",
        reason = "A cross-team architecture decision was just made",
        key_points = new[] { "why now", "alternatives considered" },
    };

    // ---- finalize-turn ----------------------------------------------------------

    [Fact]
    public async Task Finalize_OfferedTurn_PrintsCompactSummary()
    {
        await using var db = await NewDbAsync();
        var payload = Payload("Design the new caching layer", "Proposed a shared caching layer across teams.", OfferPayload);
        var (code, output) = await RunWithStdinAsync(db, payload, "finalize-turn");

        Assert.Equal(0, code);
        Assert.Contains("AgentRecall Document Opportunity", output, StringComparison.Ordinal);
        Assert.Contains("Adopt the shared caching layer", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finalize_NoDocOpportunity_PrintsNoDocumentSummary()
    {
        await using var db = await NewDbAsync();
        var payload = Payload("Fix a typo in the README", "Fixed the typo.");
        var (code, output) = await RunWithStdinAsync(db, payload, "finalize-turn");

        Assert.Equal(0, code);
        Assert.DoesNotContain("AgentRecall Document Opportunity", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finalize_MalformedDocOpportunity_NeverBlocks()
    {
        await using var db = await NewDbAsync();
        var payload = Payload("Design the new caching layer", "Proposed a shared caching layer.",
            new { decision = "NotARealDecision" });
        var (code, _) = await RunWithStdinAsync(db, payload, "finalize-turn");

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task Finalize_Hook_EmitsOnlyPointer_NeverFullSummary()
    {
        await using var db = await NewDbAsync();
        var payload = Payload("Design the new caching layer", "Proposed a shared caching layer across teams.", OfferPayload);
        var (code, output) = await RunWithStdinAsync(db, payload, "finalize-turn", "--hook");

        Assert.Equal(0, code);
        // A short pointer is allowed in the Turn Memory Summary...
        Assert.Contains("RFC", output, StringComparison.Ordinal);
        Assert.Contains("Adopt the shared caching layer", output, StringComparison.Ordinal);
        // ...but never the full compact/detailed document-opportunity summary.
        Assert.DoesNotContain("AgentRecall Document Opportunity:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Covers:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finalize_PersistsCandidate_ReadableByCommand()
    {
        await using var db = await NewDbAsync();
        var payload = Payload("Design the new caching layer", "Proposed a shared caching layer across teams.", OfferPayload);
        await RunWithStdinAsync(db, payload, "finalize-turn", "--hook");

        var writer = new StringWriter();
        var code = await CommandRouter.RunAsync(["document", "status"], db.Services, writer);
        Assert.Equal(0, code);
        Assert.Contains("Adopt the shared caching layer", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finalize_NeverBlocks_EvenWithDocOpportunityEnabled()
    {
        await using var db = await NewDbAsync();
        var payload = Payload("Design the new caching layer", "Proposed a shared caching layer.", OfferPayload);
        var (code, _) = await RunWithStdinAsync(db, payload, "finalize-turn");
        Assert.Equal(0, code);
    }

    // ---- document write -----------------------------------------------------------

    private static string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentrecall-doc-write-test", Guid.NewGuid().ToString("N"));
        return dir;
    }

    [Fact]
    public async Task DocumentWrite_WritesToTypeFolder_WithDateAndSlug()
    {
        await using var db = await NewDbAsync();
        var root = NewTempRoot();
        try
        {
            var (code, output) = await RunWithStdinAsync(
                db, "# Test RFC\n\nBody.",
                "document", "write", "--type", "Rfc", "--title", "Test RFC", "--root", root);

            Assert.Equal(0, code);
            var expectedFolder = Path.Combine(root, "rfcs");
            Assert.True(Directory.Exists(expectedFolder));

            var files = Directory.GetFiles(expectedFolder);
            Assert.Single(files);
            Assert.Contains("test-rfc", files[0], StringComparison.Ordinal);
            Assert.Contains("Wrote RFC document to", output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DocumentWrite_SecondWriteSameTitle_AutoSuffixes_NeverOverwrites()
    {
        await using var db = await NewDbAsync();
        var root = NewTempRoot();
        try
        {
            await RunWithStdinAsync(db, "first content",
                "document", "write", "--type", "Incident", "--title", "Outage", "--root", root);
            await RunWithStdinAsync(db, "second content",
                "document", "write", "--type", "Incident", "--title", "Outage", "--root", root);

            var folder = Path.Combine(root, "incidents");
            var files = Directory.GetFiles(folder);
            Assert.Equal(2, files.Length);

            var datePrefix = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            var plainPath = Path.Combine(folder, $"{datePrefix}-outage.md");
            var suffixedPath = Path.Combine(folder, $"{datePrefix}-outage-2.md");

            Assert.Equal("first content", await File.ReadAllTextAsync(plainPath));
            Assert.Equal("second content", await File.ReadAllTextAsync(suffixedPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DocumentWrite_Force_OverwritesInPlace()
    {
        await using var db = await NewDbAsync();
        var root = NewTempRoot();
        try
        {
            await RunWithStdinAsync(db, "original",
                "document", "write", "--type", "Proposal", "--title", "Plan", "--root", root);
            await RunWithStdinAsync(db, "replaced",
                "document", "write", "--type", "Proposal", "--title", "Plan", "--root", root, "--force");

            var folder = Path.Combine(root, "proposals");
            var files = Directory.GetFiles(folder);
            Assert.Single(files);
            Assert.Equal("replaced", await File.ReadAllTextAsync(files[0]));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DocumentWrite_MissingType_ReturnsErrorAndUsage()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunWithStdinAsync(db, "content", "document", "write", "--title", "x");
        Assert.Equal(1, code);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentWrite_MissingTitle_ReturnsError()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunWithStdinAsync(db, "content", "document", "write", "--type", "Rfc");
        Assert.Equal(1, code);
        Assert.Contains("Missing --title", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentWrite_WithTurnId_MarksMatchingCandidateWritten()
    {
        await using var db = await NewDbAsync();
        var root = NewTempRoot();
        try
        {
            await using (var scope = db.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IDocOpportunityService>().AnalyzeTurnAsync(
                    new DocOpportunityTurnRequest
                    {
                        Prompt = "p",
                        Response = "r",
                        TurnId = "turn-abc",
                        SuppliedVerdict = new DocOpportunityVerdict
                        {
                            Decision = DocOpportunityDecision.Offer,
                            DocumentType = DocumentType.Runbook,
                            Confidence = 0.7,
                            SuggestedTitle = "Deploy rollback steps",
                        },
                    });
            }

            await RunWithStdinAsync(db, "# Rollback\n\nSteps.",
                "document", "write", "--type", "Runbook", "--title", "Deploy rollback steps",
                "--turn-id", "turn-abc", "--root", root);

            await using var readScope = db.CreateScope();
            var written = await readScope.ServiceProvider.GetRequiredService<IDocOpportunityCandidateRepository>()
                .FindByTurnAsync("turn-abc");

            Assert.NotNull(written);
            Assert.Equal(DocOpportunityStatus.Written, written!.Status);
            Assert.False(string.IsNullOrEmpty(written.WrittenPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DocumentWrite_WithoutTurnId_StillSucceedsWithNoCandidateSideEffect()
    {
        await using var db = await NewDbAsync();
        var root = NewTempRoot();
        try
        {
            var (code, _) = await RunWithStdinAsync(db, "body",
                "document", "write", "--type", "Adr", "--title", "No linkage", "--root", root);
            Assert.Equal(0, code);

            var last = await WithScopeAsync(db, sp => sp.GetRequiredService<IDocOpportunityCandidateRepository>().GetLatestAsync());
            Assert.Null(last);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<T> WithScopeAsync<T>(TestDatabase db, Func<IServiceProvider, Task<T>> body)
    {
        await using var scope = db.CreateScope();
        return await body(scope.ServiceProvider);
    }

    [Fact]
    public async Task DocumentWrite_EveryDocumentType_MapsToCorrectFolder()
    {
        await using var db = await NewDbAsync();
        var root = NewTempRoot();
        try
        {
            var types = new[] { "Incident", "Rfc", "Proposal", "Adr", "Postmortem", "Runbook" };
            var expectedFolders = new[] { "incidents", "rfcs", "proposals", "adrs", "postmortems", "runbooks" };

            for (var i = 0; i < types.Length; i++)
            {
                await RunWithStdinAsync(db, "body",
                    "document", "write", "--type", types[i], "--title", $"Doc {i}", "--root", root);
                Assert.True(Directory.Exists(Path.Combine(root, expectedFolders[i])), $"missing folder for {types[i]}");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
