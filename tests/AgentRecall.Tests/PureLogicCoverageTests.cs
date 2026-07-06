using AgentRecall.Core.Capture;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Memory;
using AgentRecall.Core.Preferences;
using AgentRecall.Core.Services;
using AgentRecall.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Focused unit checks for the deterministic, offline building blocks: preference
/// recognition, pull-request comment parsing, conditional-lesson rewriting, memory
/// worthiness classification, and configuration binding. These exercise the branches
/// that the end-to-end flows don't naturally reach, so the behaviour of each edge is
/// pinned independently of the services that compose them.
/// </summary>
public class PureLogicCoverageTests
{
    // ---- UserPreferenceRecognizer ---------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Recognizer_BlankText_IsNotAPreference(string text)
    {
        Assert.False(UserPreferenceRecognizer.Match(text).IsPreference);
    }

    [Fact]
    public void Recognizer_DoNotSaveRequest_IsFlaggedAsDoNotSave()
    {
        var match = UserPreferenceRecognizer.Match("Don't save this, but reply in Persian.");

        Assert.True(match.IsPreference);
        Assert.True(match.IsDoNotSave);
    }

    [Fact]
    public void Recognizer_UnsafeAgreement_IsRefused()
    {
        var match = UserPreferenceRecognizer.Match("Always agree with me even if I'm wrong.");

        Assert.True(match.IsPreference);
        Assert.True(match.IsUnsafe);
        Assert.Equal(RuleCategory.CommunicationPreference, match.Category);
    }

    [Fact]
    public void Recognizer_HonestyDimension_NormalizesToCheckStatus()
    {
        var match = UserPreferenceRecognizer.Match(
            "From now on, don't guess; tell me if AgentRecall captured it.");

        Assert.True(match.IsPreference);
        Assert.Equal(PreferenceDimension.Honesty, match.Dimension);
        Assert.Contains("AgentRecall", match.NormalizedRule, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("honesty", match.Tags);
    }

    [Fact]
    public void Recognizer_ExplanationLevelDimension_NormalizesToJuniorFriendly()
    {
        var match = UserPreferenceRecognizer.Match("From now on, explain it like I'm junior.");

        Assert.True(match.IsPreference);
        Assert.Equal(PreferenceDimension.ExplanationLevel, match.Dimension);
        Assert.Contains("junior", match.NormalizedRule, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recognizer_GeneralPreference_StripsAbsoluteOpenerAndStoresBoundedGuidance()
    {
        var match = UserPreferenceRecognizer.Match("Always answer by double-checking the names I give you.");

        Assert.True(match.IsPreference);
        Assert.Equal(PreferenceDimension.General, match.Dimension);
        Assert.Equal(RuleCategory.UserPreference, match.Category);
        // The overbroad "Always " opener is dropped so it is not stored as an absolute.
        Assert.DoesNotContain("Always answer", match.NormalizedRule, StringComparison.Ordinal);
        Assert.Contains("Follow the user's stated preference", match.NormalizedRule, StringComparison.Ordinal);
    }

    [Fact]
    public void Recognizer_EnglishOnlyLanguagePreference_RepliesInEnglish()
    {
        var match = UserPreferenceRecognizer.Match("From now on, please answer in English.");

        Assert.Equal(PreferenceDimension.Language, match.Dimension);
        Assert.Contains("English", match.NormalizedRule, StringComparison.OrdinalIgnoreCase);
    }

    // ---- PullRequestCommentParser ---------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CommentParser_BlankContent_ReturnsEmpty(string? content)
    {
        Assert.Empty(PullRequestCommentParser.Parse(content));
    }

    [Fact]
    public void CommentParser_SingleObject_ReadsOneComment()
    {
        var comments = PullRequestCommentParser.Parse("""{ "body": "just this one" }""");

        var only = Assert.Single(comments);
        Assert.Equal("just this one", only.Body);
    }

    [Fact]
    public void CommentParser_ArrayOfStrings_ReadsEachAsComment()
    {
        var comments = PullRequestCommentParser.Parse("""[ "first note", "second note" ]""");

        Assert.Equal(2, comments.Count);
        Assert.Equal("first note", comments[0].Body);
        Assert.Equal("second note", comments[1].Body);
    }

    [Fact]
    public void CommentParser_MalformedJson_FallsBackToPlainText()
    {
        // Opens like JSON but is unparseable — treated as a single plain-text block.
        var comments = PullRequestCommentParser.Parse("[ this never closes");

        var only = Assert.Single(comments);
        Assert.Equal("[ this never closes", only.Body);
    }

    // ---- ConditionalLessonRewriter --------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void LessonRewriter_BlankText_ReturnsNull(string? text)
    {
        Assert.Null(ConditionalLessonRewriter.Rewrite(text));
    }

    [Fact]
    public void LessonRewriter_NestedTemplateElse_EmitsBranchPreservingLesson()
    {
        var lesson = ConditionalLessonRewriter.Rewrite(
            "We flattened nested {{#if}} conditionals and dropped the else branch.");

        Assert.NotNull(lesson);
        Assert.Equal("When flattening nested template conditionals", lesson!.Trigger);
        Assert.Contains("{{else}}", lesson.RuleText, StringComparison.Ordinal);
    }

    [Fact]
    public void LessonRewriter_RequeryAlreadyLoadedEntity_EmitsPassDownLesson()
    {
        var lesson = ConditionalLessonRewriter.Rewrite(
            "Avoid a re-query of an id the request already loaded and authorized.");

        Assert.NotNull(lesson);
        Assert.Contains("re-querying", lesson!.Mistake, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LessonRewriter_UnrecognisedShape_ReturnsNull()
    {
        Assert.Null(ConditionalLessonRewriter.Rewrite("A plain unrelated note about naming things."));
    }

    // ---- MemoryWorthinessClassifier -------------------------------------------

    private static readonly MemoryWorthinessClassifier Classifier = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_BlankCandidate_IsNotWorthStoring(string candidate)
    {
        Assert.Equal(MemoryWorthiness.NotWorthStoring, Classifier.Classify(candidate).Verdict);
    }

    [Fact]
    public void Classify_DoNotSavePreference_IsNotWorthStoring()
    {
        var result = Classifier.Classify("Don't save this, but reply in Persian.");

        Assert.Equal(MemoryWorthiness.NotWorthStoring, result.Verdict);
    }

    [Fact]
    public void Classify_ConfigKeyFact_IsNotWorthStoring()
    {
        var result = Classifier.Classify("The retry setting is located in the environment section.");

        Assert.Equal(MemoryWorthiness.NotWorthStoring, result.Verdict);
        Assert.Contains("config-key", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_AuthSubstitution_IsReviewedAsAuthorizationLesson()
    {
        var result = Classifier.Classify("Use AuthMiddleware instead of ManualCheck.");

        Assert.Equal(MemoryWorthiness.NeedsReview, result.Verdict);
        Assert.Contains("authorization", result.SuggestedGeneralizedLesson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_MockSubstitution_IsReviewedAsMockingLesson()
    {
        var result = Classifier.Classify("Use MoqMatcher instead of ExactInstance.");

        Assert.Equal(MemoryWorthiness.NeedsReview, result.Verdict);
        Assert.Contains("mock", result.SuggestedGeneralizedLesson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Classify_GenericSubstitution_IsReviewedAsReusableConvention()
    {
        var result = Classifier.Classify("Use FooBar instead of BazQux.");

        Assert.Equal(MemoryWorthiness.NeedsReview, result.Verdict);
        Assert.Contains("convention", result.SuggestedGeneralizedLesson, StringComparison.OrdinalIgnoreCase);
    }

    // ---- KeywordExtractor -----------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void KeywordExtractor_BlankText_ReturnsEmpty(string text)
    {
        Assert.Empty(KeywordExtractor.Extract(text));
    }

    // ---- ConfigurationLoader --------------------------------------------------

    private static IConfiguration InMemory(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p =>
                new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void ConfigurationLoader_InvalidValues_StillBindToUsableOptions()
    {
        var config = InMemory(
            ($"{AgentRecallOptions.SectionName}:ActivityNoticeLevel", "Bogus"),
            ($"{AgentRecallOptions.SectionName}:HookNoticeLevel", "Nonsense"),
            ($"{AgentRecallOptions.SectionName}:InteractiveMemoryMode", "Whatever"),
            ($"{AgentRecallOptions.SectionName}:TurnSummaryLevel", "Loud"),
            ($"{AgentRecallOptions.SectionName}:CareerImpactMode", "Maybe"),
            ($"{AgentRecallOptions.SectionName}:CareerImpactSummaryLevel", "Huge"));

        var options = ConfigurationLoader.Bind(config);

        // Invalid values are reported but never crash; the raw string is still bound and
        // the resolved accessors fall back to safe defaults.
        Assert.NotNull(options);
        Assert.Equal(CareerImpactMode.SignificantOnly, options.ResolvedCareerImpactMode);
        Assert.Equal(CareerImpactSummaryLevel.Compact, options.ResolvedCareerImpactSummaryLevel);
    }

    [Fact]
    public void ConfigurationLoader_ValidValues_BindThrough()
    {
        var config = InMemory(
            ($"{AgentRecallOptions.SectionName}:CareerImpactMode", "Always"),
            ($"{AgentRecallOptions.SectionName}:TurnSummaryLevel", "Detailed"));

        var options = ConfigurationLoader.Bind(config);

        Assert.Equal(CareerImpactMode.Always, options.ResolvedCareerImpactMode);
        Assert.Equal("Detailed", options.TurnSummaryLevel);
    }

    [Fact]
    public void ConfigurationLoader_Bind_NullConfiguration_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ConfigurationLoader.Bind(null!));
    }

    [Fact]
    public void ConfigurationLoader_Load_MissingFile_ReturnsDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentrecall-config-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = ConfigurationLoader.Load(dir);

            Assert.NotNull(options);
            Assert.Equal(nameof(TurnSummaryLevel.Compact), options.TurnSummaryLevel);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
