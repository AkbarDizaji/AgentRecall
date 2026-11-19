using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.CareerImpact;
using AgentRecall.Core.DocOpportunity;
using AgentRecall.Core.Domain;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// The verdict/analysis ↔ candidate mappings were only exercised through the services that
/// persist candidates. Their storage encoding — newline-joined lists, comma-joined category
/// names — and the defaults they fill in are pinned here against the mappers themselves.
/// </summary>
public class CandidateMappingTests
{
    [Fact]
    public void Doc_verdict_maps_to_an_open_candidate_with_trimmed_text()
    {
        var verdict = new DocOpportunityVerdict
        {
            Decision = DocOpportunityDecision.Offer,
            DocumentType = DocumentType.Postmortem,
            Confidence = 0.8,
            SuggestedTitle = "  Queue outage  ",
            Reason = "\tworth writing down\n",
            KeyPoints = ["timeline", "root cause"],
        };

        var entity = DocOpportunityMapping.ToEntity(verdict, "turn-1", "hash-1");

        Assert.Equal("turn-1", entity.TurnId);
        Assert.Equal("hash-1", entity.OperationHash);
        Assert.Equal(DocumentType.Postmortem, entity.DocumentType);
        Assert.Equal(0.8, entity.Confidence);
        Assert.Equal("Queue outage", entity.SuggestedTitle);
        Assert.Equal("worth writing down", entity.Reason);
        Assert.Equal("timeline\nroot cause", entity.KeyPoints);
        Assert.Equal("HostSuppliedDocOpportunityJudge", entity.Source);
        Assert.Equal(DocOpportunityStatus.Open, entity.Status);
    }

    [Fact]
    public void Doc_verdict_without_turn_title_or_reason_stores_empty_strings()
    {
        var entity = DocOpportunityMapping.ToEntity(new DocOpportunityVerdict(), null, "hash");

        Assert.Equal(string.Empty, entity.TurnId);
        Assert.Equal(string.Empty, entity.SuggestedTitle);
        Assert.Equal(string.Empty, entity.Reason);
        Assert.Equal(string.Empty, entity.KeyPoints);
        Assert.Empty(DocOpportunityMapping.KeyPoints(entity));
    }

    [Fact]
    public void Doc_key_points_drop_blank_lines_and_trim_each_point()
    {
        var entity = new DocOpportunityCandidate { KeyPoints = " timeline \n\n   \nroot cause" };

        Assert.Equal(["timeline", "root cause"], DocOpportunityMapping.KeyPoints(entity));
    }

    [Fact]
    public void Doc_mapping_rejects_null_inputs()
    {
        Assert.Throws<ArgumentNullException>(() => DocOpportunityMapping.ToEntity(null!, "t", "h"));
        Assert.Throws<ArgumentNullException>(() => DocOpportunityMapping.KeyPoints(null!));
    }

    [Fact]
    public void Career_analysis_round_trips_through_the_candidate()
    {
        var analysis = new CareerImpactAnalysis
        {
            IsSignificant = true,
            HasSignal = true,
            Confidence = 0.9,
            PromotionWorthiness = 4,
            Categories = [ImpactCategory.Reliability, ImpactCategory.Cost],
            Reasons = ["cut p99 latency", "removed a paging alert"],
            SuggestedMetrics = ["p99 before/after"],
            SuggestedEvidence = ["dashboard link"],
            Stakeholders = ["SRE"],
            NextActions = ["write it up"],
            Adr = new CareerImpactAdr
            {
                Recommended = true,
                SuggestedTitle = "Move to a bounded queue",
                Context = "unbounded growth",
                Decision = "cap at 10k",
                Alternatives = ["autoscale", "drop oldest"],
                Consequences = ["back-pressure on producers"],
            },
            WhyThisMatters = "fewer incidents",
            TechnicalImpact = "tech",
            BusinessImpact = "biz",
            LongTermImpact = "long",
            PromotionNote = "note",
        };

        var entity = CareerImpactMapping.ToEntity(analysis, "turn-7", "hash-7", "cli");
        var back = CareerImpactMapping.ToAnalysis(entity);

        Assert.Equal("Reliability,Cost", entity.Categories);
        Assert.Equal("cut p99 latency\nremoved a paging alert", entity.Reasons);
        Assert.Equal("cli", entity.Source);
        Assert.Equal(CareerImpactStatus.Open, entity.Status);

        Assert.True(back.IsSignificant);
        Assert.True(back.HasSignal);
        Assert.Equal(0.9, back.Confidence);
        Assert.Equal(4, back.PromotionWorthiness);
        Assert.Equal(analysis.Categories, back.Categories);
        Assert.Equal(analysis.Reasons, back.Reasons);
        Assert.Equal(analysis.SuggestedMetrics, back.SuggestedMetrics);
        Assert.Equal(analysis.SuggestedEvidence, back.SuggestedEvidence);
        Assert.Equal(analysis.Stakeholders, back.Stakeholders);
        Assert.Equal(analysis.NextActions, back.NextActions);
        Assert.Equal(analysis.Adr.Recommended, back.Adr.Recommended);
        Assert.Equal(analysis.Adr.SuggestedTitle, back.Adr.SuggestedTitle);
        Assert.Equal(analysis.Adr.Context, back.Adr.Context);
        Assert.Equal(analysis.Adr.Decision, back.Adr.Decision);
        Assert.Equal(analysis.Adr.Alternatives, back.Adr.Alternatives);
        Assert.Equal(analysis.Adr.Consequences, back.Adr.Consequences);
        Assert.Equal("fewer incidents", back.WhyThisMatters);
        Assert.Equal("tech", back.TechnicalImpact);
        Assert.Equal("biz", back.BusinessImpact);
        Assert.Equal("long", back.LongTermImpact);
        Assert.Equal("note", back.PromotionNote);
        Assert.Equal("possible Staff-level impact detected", back.CompactSummary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Career_candidate_without_a_source_is_attributed_to_the_detector(string? source) =>
        Assert.Equal(
            "CareerImpactDetector",
            CareerImpactMapping.ToEntity(new CareerImpactAnalysis(), null, "h", source!).Source);

    [Fact]
    public void Career_candidate_without_a_turn_stores_an_empty_turn_id() =>
        Assert.Equal(string.Empty, CareerImpactMapping.ToEntity(new CareerImpactAnalysis(), null, "h", "cli").TurnId);

    [Fact]
    public void Career_categories_skip_names_this_build_does_not_know()
    {
        var entity = new CareerImpactCandidate { Categories = "Security, NotACategory ,,Performance" };

        Assert.Equal(
            [ImpactCategory.Security, ImpactCategory.Performance],
            CareerImpactMapping.ToAnalysis(entity).Categories);
    }

    [Fact]
    public void Career_candidate_with_empty_columns_maps_to_empty_lists()
    {
        var back = CareerImpactMapping.ToAnalysis(new CareerImpactCandidate());

        Assert.Empty(back.Categories);
        Assert.Empty(back.Reasons);
        Assert.Empty(back.SuggestedMetrics);
        Assert.Empty(back.Adr.Alternatives);
        Assert.Empty(back.Adr.Consequences);
    }

    [Fact]
    public void Insignificant_career_candidate_gets_the_softer_summary() =>
        Assert.Equal(
            "possible engineering impact detected",
            CareerImpactMapping.ToAnalysis(new CareerImpactCandidate { IsSignificant = false }).CompactSummary);

    [Fact]
    public void Career_mapping_rejects_null_inputs()
    {
        Assert.Throws<ArgumentNullException>(() => CareerImpactMapping.ToEntity(null!, "t", "h", "cli"));
        Assert.Throws<ArgumentNullException>(() => CareerImpactMapping.ToAnalysis(null!));
    }
}
