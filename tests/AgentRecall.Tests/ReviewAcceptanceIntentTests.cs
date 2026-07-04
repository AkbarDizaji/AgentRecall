using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Covers the shared <see cref="ReviewAcceptanceIntent"/> detector and its use in the turn
/// finalizer, so the finalize-turn path recognises the same review-acceptance phrasings as
/// the Stop-hook capture path (both now share the regex).
/// </summary>
public class ReviewAcceptanceIntentTests
{
    [Theory] // Intervening words that the old fixed phrases missed are now caught.
    [InlineData("Please apply the reviewer's second comment")]
    [InlineData("Apply the review comment")]
    [InlineData("Do exactly what the reviewer said")]
    [InlineData("Fix this per the review feedback")]
    [InlineData("Following the review suggestions, add pagination")]
    [InlineData("Based on the review, validate inputs")]
    [InlineData("As suggested in the review, cache the result")]
    [InlineData("As the reviewer noted, guard the null case")]
    public void Matches_ReviewAcceptancePhrasings(string text) =>
        Assert.True(ReviewAcceptanceIntent.Matches(text), text);

    [Theory] // Ordinary work / corrections without review-acceptance intent are not matched.
    [InlineData("Add a new endpoint for users")]
    [InlineData("We do not mock DbContext directly")]
    [InlineData("Always validate inputs at the API boundary")]
    [InlineData("Refactor the migration for readability")]
    [InlineData("")]
    [InlineData(null)]
    public void DoesNotMatch_NonAcceptance(string? text) =>
        Assert.False(ReviewAcceptanceIntent.Matches(text));

    private static ITurnCandidateExtractor Extractor()
    {
        // Resolve the real extractor (with its analyzer dependency) from the container.
        var services = new ServiceCollection();
        services.AddSingleton(new Core.Configuration.AgentRecallOptions());
        services.AddSingleton<IFeedbackCandidateAnalyzer, Core.Services.FeedbackCandidateAnalyzer>();
        services.AddSingleton<ITurnCandidateExtractor, TurnCandidateExtractor>();
        return services.BuildServiceProvider().GetRequiredService<ITurnCandidateExtractor>();
    }

    [Fact] // Finalizer parity: an intervening-word acceptance is detected here too.
    public void Extractor_DetectsReviewAccepted_WithInterveningWords()
    {
        var signals = Extractor().DetectOutcomeSignals(
            "Please apply the reviewer's second comment about tenant scope", assistantText: null);
        Assert.True(signals.ReviewAccepted);
    }

    [Fact] // The verb-trails-noun phrasing (regex can't catch it) still works via the kept phrases.
    public void Extractor_DetectsReviewAccepted_VerbTrailsNoun()
    {
        var signals = Extractor().DetectOutcomeSignals(
            "The review comment was applied and the change is in", assistantText: null);
        Assert.True(signals.ReviewAccepted);
    }

    [Fact] // "do not save this" is never misread as acceptance.
    public void Extractor_DoNotSave_IsNotAcceptance()
    {
        Assert.False(Extractor().HasAcceptanceSignal("Do not save this, it's a one-off"));
    }

    [Fact] // A plain "save this" acceptance still works.
    public void Extractor_SaveThis_IsAcceptance()
    {
        Assert.True(Extractor().HasAcceptanceSignal("Please save this rule for next time"));
    }
}
