using System.Text.Json;
using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.DocOpportunity;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Covers the document-opportunity feature end-to-end minus the finalize-turn stdin path
/// (see <see cref="DocOpportunityStdinTests"/>): the validator, the slug utility, the
/// renderer's bounded pointer, the mode-gated service, the repository's Id-descending
/// ordering, and the on-demand <c>document status</c> command. Everything runs against an
/// isolated temp database and never touches the network, an LLM, or embeddings.
/// </summary>
public class DocOpportunityTests
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

    private static async Task<T> WithScopeAsync<T>(TestDatabase db, Func<IServiceProvider, Task<T>> body)
    {
        await using var scope = db.CreateScope();
        return await body(scope.ServiceProvider);
    }

    // ---- Validator --------------------------------------------------------------

    [Fact]
    public void Validator_OfferWithoutTitle_IsInvalid()
    {
        var verdict = new DocOpportunityVerdict { Decision = DocOpportunityDecision.Offer, DocumentType = DocumentType.Rfc, Confidence = 0.8 };
        Assert.False(DocOpportunityValidator.Validate(verdict).IsValid);
    }

    [Fact]
    public void Validator_OfferWithUndefinedDocumentType_IsInvalid()
    {
        var verdict = new DocOpportunityVerdict
        {
            Decision = DocOpportunityDecision.Offer,
            DocumentType = (DocumentType)999,
            Confidence = 0.8,
            SuggestedTitle = "x",
        };
        Assert.False(DocOpportunityValidator.Validate(verdict).IsValid);
    }

    [Fact]
    public void Validator_OfferWithTitleAndType_IsValid()
    {
        var verdict = new DocOpportunityVerdict
        {
            Decision = DocOpportunityDecision.Offer,
            DocumentType = DocumentType.Incident,
            Confidence = 0.5,
            SuggestedTitle = "Outage postmortem",
        };
        Assert.True(DocOpportunityValidator.Validate(verdict).IsValid);
    }

    [Fact]
    public void Validator_SkipWithoutWhyNotOffered_IsInvalid()
    {
        var verdict = new DocOpportunityVerdict { Decision = DocOpportunityDecision.Skip, Confidence = 0.2 };
        Assert.False(DocOpportunityValidator.Validate(verdict).IsValid);
    }

    [Fact]
    public void Validator_SkipWithWhyNotOffered_IsValid()
    {
        var verdict = new DocOpportunityVerdict { Decision = DocOpportunityDecision.Skip, Confidence = 0.2, WhyNotOffered = "routine" };
        Assert.True(DocOpportunityValidator.Validate(verdict).IsValid);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Validator_ConfidenceOutOfRange_IsInvalid(double confidence)
    {
        var verdict = new DocOpportunityVerdict { Decision = DocOpportunityDecision.Skip, Confidence = confidence, WhyNotOffered = "x" };
        Assert.False(DocOpportunityValidator.Validate(verdict).IsValid);
    }

    // ---- DocSlug ------------------------------------------------------------------

    [Theory]
    [InlineData("Adopt the Shared Caching Layer", "adopt-the-shared-caching-layer")]
    [InlineData("  leading/trailing -- spaces  ", "leading-trailing-spaces")]
    [InlineData("Already-slugged-title", "already-slugged-title")]
    public void DocSlug_Slugify_ProducesExpectedSlug(string title, string expected)
    {
        Assert.Equal(expected, DocSlug.Slugify(title));
    }

    [Fact]
    public void DocSlug_Slugify_AllPunctuation_FallsBackToUntitled()
    {
        Assert.Equal(DocSlug.Fallback, DocSlug.Slugify("!!! ??? ---"));
    }

    [Fact]
    public void DocSlug_Slugify_EmptyOrNull_FallsBackToUntitled()
    {
        Assert.Equal(DocSlug.Fallback, DocSlug.Slugify(""));
        Assert.Equal(DocSlug.Fallback, DocSlug.Slugify(null));
    }

    [Fact]
    public void DocSlug_Slugify_RespectsMaxLength()
    {
        var slug = DocSlug.Slugify(new string('a', 200), maxLength: 20);
        Assert.True(slug.Length <= 20);
    }

    [Fact]
    public void DocSlug_Slugify_UnicodeAndEmoji_NeverThrows()
    {
        var slug = DocSlug.Slugify("Café \U0001F600 review");
        Assert.False(string.IsNullOrEmpty(slug));
    }

    [Fact]
    public void DocSlug_Slugify_IsDeterministic()
    {
        var a = DocSlug.Slugify("Some Title Here");
        var b = DocSlug.Slugify("Some Title Here");
        Assert.Equal(a, b);
    }

    // ---- Renderer -------------------------------------------------------------------

    [Fact]
    public void BuildTurnSummaryPointer_IsSingleLineAndBounded()
    {
        var pointer = DocOpportunityRenderer.BuildTurnSummaryPointer(DocumentType.Rfc, "Adopt shared caching", 0.8);
        Assert.DoesNotContain("\n", pointer, StringComparison.Ordinal);
        Assert.Contains("RFC", pointer, StringComparison.Ordinal);
        Assert.Contains("Adopt shared caching", pointer, StringComparison.Ordinal);
    }

    // ---- DocumentTypeNames ------------------------------------------------------------

    [Theory]
    [InlineData(DocumentType.Incident, "incidents")]
    [InlineData(DocumentType.Rfc, "rfcs")]
    [InlineData(DocumentType.Proposal, "proposals")]
    [InlineData(DocumentType.Adr, "adrs")]
    [InlineData(DocumentType.Postmortem, "postmortems")]
    [InlineData(DocumentType.Runbook, "runbooks")]
    public void DocumentTypeNames_FolderName_MapsEveryType(DocumentType type, string expectedFolder)
    {
        Assert.Equal(expectedFolder, DocumentTypeNames.FolderName(type));
    }

    // ---- Service: mode gating and persistence ---------------------------------------

    private static async Task<DocOpportunityCandidate?> AnalyzeAsync(
        TestDatabase db, DocOpportunityVerdict? verdict, string turnId = "t1", string prompt = "p", string response = "r") =>
        await WithScopeAsync(db, sp => sp.GetRequiredService<IDocOpportunityService>()
            .AnalyzeTurnAsync(new DocOpportunityTurnRequest { Prompt = prompt, Response = response, TurnId = turnId, SuppliedVerdict = verdict }));

    private static DocOpportunityVerdict OfferVerdict(string title = "Adopt shared caching", DocumentType type = DocumentType.Rfc) =>
        new()
        {
            Decision = DocOpportunityDecision.Offer,
            DocumentType = type,
            Confidence = 0.8,
            SuggestedTitle = title,
            Reason = "why",
            KeyPoints = ["a", "b"],
        };

    [Fact]
    public async Task AnalyzeTurn_OffMode_NeverSurfaces()
    {
        await using var db = await NewDbAsync(o => o.DocOpportunityMode = "Off");
        var candidate = await AnalyzeAsync(db, OfferVerdict());
        Assert.Null(candidate);
    }

    [Fact]
    public async Task AnalyzeTurn_NullVerdict_JudgeUnavailable_ReturnsNull()
    {
        await using var db = await NewDbAsync();
        var candidate = await AnalyzeAsync(db, verdict: null);
        Assert.Null(candidate);
    }

    [Fact]
    public async Task AnalyzeTurn_SkipVerdict_PersistsNothing()
    {
        await using var db = await NewDbAsync();
        var verdict = new DocOpportunityVerdict { Decision = DocOpportunityDecision.Skip, Confidence = 0.1, WhyNotOffered = "routine" };
        var candidate = await AnalyzeAsync(db, verdict);
        Assert.Null(candidate);
    }

    [Fact]
    public async Task AnalyzeTurn_OfferVerdict_Persists()
    {
        await using var db = await NewDbAsync();
        var candidate = await AnalyzeAsync(db, OfferVerdict());
        Assert.NotNull(candidate);
        Assert.Equal(DocumentType.Rfc, candidate!.DocumentType);
        Assert.Equal(DocOpportunityStatus.Open, candidate.Status);
    }

    [Fact]
    public async Task AnalyzeTurn_InvalidOfferVerdict_MissingTitle_ReturnsNull()
    {
        await using var db = await NewDbAsync();
        var verdict = new DocOpportunityVerdict { Decision = DocOpportunityDecision.Offer, DocumentType = DocumentType.Rfc, Confidence = 0.8 };
        var candidate = await AnalyzeAsync(db, verdict);
        Assert.Null(candidate);
    }

    [Fact]
    public async Task AnalyzeTurn_IsIdempotent()
    {
        await using var db = await NewDbAsync();
        await AnalyzeAsync(db, OfferVerdict(), turnId: "t", prompt: "same", response: "same");
        await AnalyzeAsync(db, OfferVerdict(), turnId: "t", prompt: "same", response: "same");

        var all = await WithScopeAsync(db, sp => sp.GetRequiredService<IDocOpportunityCandidateRepository>().ListAsync());
        Assert.Single(all);
    }

    [Fact]
    public async Task MarkWrittenAsync_TransitionsOpenToWritten()
    {
        await using var db = await NewDbAsync();
        var candidate = await AnalyzeAsync(db, OfferVerdict());
        Assert.NotNull(candidate);

        var updated = await WithScopeAsync(db, sp => sp.GetRequiredService<IDocOpportunityService>()
            .MarkWrittenAsync(candidate!.Id, "/tmp/docs/rfcs/2026-01-01-x.md"));

        Assert.NotNull(updated);
        Assert.Equal(DocOpportunityStatus.Written, updated!.Status);
        Assert.Equal("/tmp/docs/rfcs/2026-01-01-x.md", updated.WrittenPath);
    }

    [Fact]
    public async Task MarkWrittenAsync_UnknownId_ReturnsNull()
    {
        await using var db = await NewDbAsync();
        var updated = await WithScopeAsync(db, sp => sp.GetRequiredService<IDocOpportunityService>().MarkWrittenAsync(99999, "/tmp/x.md"));
        Assert.Null(updated);
    }

    // ---- Repository ordering ---------------------------------------------------------

    [Fact]
    public async Task Repository_OrdersByIdDescending_NotCreatedAt()
    {
        await using var db = await NewDbAsync();
        await AnalyzeAsync(db, OfferVerdict("first"), turnId: "t1", prompt: "p1");
        var second = await AnalyzeAsync(db, OfferVerdict("second"), turnId: "t2", prompt: "p2");

        var last = await WithScopeAsync(db, sp => sp.GetRequiredService<IDocOpportunityCandidateRepository>().GetLatestAsync());
        Assert.Equal(second!.Id, last!.Id);
    }

    // ---- document status command ------------------------------------------------

    [Fact]
    public async Task DocumentStatus_NoCandidate_ReportsNone()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunAsync(db, "document", "status");
        Assert.Equal(0, code);
        Assert.Contains("none offered yet", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DocumentStatus_WithCandidate_RendersIt()
    {
        await using var db = await NewDbAsync();
        await AnalyzeAsync(db, OfferVerdict("Adopt shared caching"));
        var (code, output) = await RunAsync(db, "document", "status");
        Assert.Equal(0, code);
        Assert.Contains("Adopt shared caching", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentStatus_Json_IsValid()
    {
        await using var db = await NewDbAsync();
        await AnalyzeAsync(db, OfferVerdict());
        var (code, output) = await RunAsync(db, "document", "status", "--json");
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(output);
        var last = doc.RootElement.GetProperty("last_candidate");
        Assert.Equal("Rfc", last.GetProperty("document_type").GetString());

        // No rendered-notice Markdown leaks into the structured output.
        Assert.DoesNotContain("📄", output, StringComparison.Ordinal);
        Assert.DoesNotContain("**", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentStatus_Off_ReportsModeButNoCandidate()
    {
        await using var db = await NewDbAsync(o => o.DocOpportunityMode = "Off");
        var (code, output) = await RunAsync(db, "document", "status");
        Assert.Equal(0, code);
        Assert.Contains("Off", output, StringComparison.Ordinal);
    }
}
