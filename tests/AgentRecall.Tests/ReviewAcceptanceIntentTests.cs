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

    // ============================================================================
    // Constructor guard.
    // ============================================================================

    [Fact] // Block-removal on the ctor body would leave _analyzer null instead of throwing.
    public void Constructor_Throws_WhenAnalyzerIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new TurnCandidateExtractor(null!));
    }

    // ============================================================================
    // HasDoNotSaveSignal: OR across user/assistant text (kills the OR->AND mutation).
    // ============================================================================

    [Fact]
    public void HasDoNotSaveSignal_True_WhenOnlyUserTextHasPhrase()
    {
        Assert.True(Extractor().HasDoNotSaveSignal("Please do not save this rule", assistantText: null));
    }

    [Fact]
    public void HasDoNotSaveSignal_True_WhenOnlyAssistantTextHasPhrase()
    {
        Assert.True(Extractor().HasDoNotSaveSignal("Looks good, ship it", "Note: do not save this as a rule"));
    }

    [Fact]
    public void HasDoNotSaveSignal_False_WhenNeitherTextHasPhrase()
    {
        Assert.False(Extractor().HasDoNotSaveSignal("Looks good, ship it", "Great, will do"));
    }

    // ============================================================================
    // HasAcceptanceSignal.
    // ============================================================================

    [Theory] // The null/whitespace guard must return false, not the mutated "true".
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HasAcceptanceSignal_False_ForNullOrWhitespace(string? text)
    {
        Assert.False(Extractor().HasAcceptanceSignal(text));
    }

    [Fact] // Stripping a do-not-save marker must leave a SPACE, not "", or removing it can
           // splice the surrounding text into an accidental "save this" match.
    public void HasAcceptanceSignal_MarkerStrippedWithSpace_DoesNotCreateFalseMatch()
    {
        // "sav" + "skip memory" (a do-not-save marker) + "e this is great": replacing the
        // marker with " " leaves "sav e this is great" (no match). Replacing it with "" would
        // splice "sav" directly onto "e this is great", spelling "save this is great" — a
        // spurious "save this" acceptance match that was never really there.
        Assert.False(Extractor().HasAcceptanceSignal("savskip memorye this is great"));
    }

    // ============================================================================
    // SaveIntentFollowsDoNotSave.
    // ============================================================================

    [Theory] // The null/whitespace guard must return false, not the mutated "true".
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SaveIntentFollowsDoNotSave_False_ForNullOrWhitespace(string? text)
    {
        Assert.False(Extractor().SaveIntentFollowsDoNotSave(text));
    }

    [Fact] // The masking loop's "idx > 0" (mutated) would skip masking a marker that starts at
           // index 0, leaving "do not save this" literally intact — and "save this" (a
           // SaveIntentSignals phrase) is embedded right inside that unmasked marker text, so
           // the mutant would wrongly report a later "save" intent when there is none.
    public void SaveIntentFollowsDoNotSave_False_WhenSaveIntentIsOnlyEmbeddedInsideMarker()
    {
        Assert.False(Extractor().SaveIntentFollowsDoNotSave("do not save this, thanks"));
    }

    [Fact] // Regression guard for LastIndexOfAny's "not found" sentinel: a lone save-intent
           // phrase at index 0 with no do-not-save phrase anywhere must still be honoured.
    public void SaveIntentFollowsDoNotSave_True_ForLoneSaveIntentAtStartOfText()
    {
        Assert.True(Extractor().SaveIntentFollowsDoNotSave("save this later"));
    }

    // ============================================================================
    // DetectOutcomeSignals.
    // ============================================================================

    [Fact] // HasAny's ">= 2" boundary: exactly 2 must count, not just "> 2".
    public void TurnOutcomeSignals_HasAny_True_WhenRepeatedCorrectionCountIsExactlyTwo()
    {
        var signals = new TurnOutcomeSignals { RepeatedCorrectionCount = 2 };
        Assert.True(signals.HasAny);
    }

    [Fact]
    public void TurnOutcomeSignals_HasAny_False_WhenRepeatedCorrectionCountIsOne()
    {
        var signals = new TurnOutcomeSignals { RepeatedCorrectionCount = 1 };
        Assert.False(signals.HasAny);
    }

    [Fact] // OR across user/assistant text for the repeated-correction signal.
    public void DetectOutcomeSignals_RepeatedCorrection_DetectedFromUserTextAlone()
    {
        var signals = Extractor().DetectOutcomeSignals(
            "This is the same mistake again, please fix it properly", assistantText: null);
        Assert.Equal(2, signals.RepeatedCorrectionCount);
        Assert.True(signals.HasAny);
    }

    [Fact]
    public void DetectOutcomeSignals_RepeatedCorrection_DetectedFromAssistantTextAlone()
    {
        var signals = Extractor().DetectOutcomeSignals("Looks fine now", "This is the same mistake again in this code");
        Assert.Equal(2, signals.RepeatedCorrectionCount);
    }

    [Fact] // The (repeated ? 2 : 0) ternary must actually branch, not collapse to always-0.
    public void DetectOutcomeSignals_RepeatedCorrectionCount_ZeroWhenNoRepeatPhrasePresent()
    {
        var signals = Extractor().DetectOutcomeSignals("Everything looks good", assistantText: null);
        Assert.Equal(0, signals.RepeatedCorrectionCount);
    }

    [Fact] // OR across user/assistant text for the test-failed-then-fixed signal.
    public void DetectOutcomeSignals_TestFailedThenFixed_DetectedFromUserTextAlone()
    {
        var signals = Extractor().DetectOutcomeSignals(
            "The tests failed because of a null check, now fixed", assistantText: null);
        Assert.True(signals.TestFailedThenFixed);
    }

    [Fact]
    public void DetectOutcomeSignals_TestFailedThenFixed_DetectedFromAssistantTextAlone()
    {
        var signals = Extractor().DetectOutcomeSignals("Great, ship it", "The test was red until I fixed the null check");
        Assert.True(signals.TestFailedThenFixed);
    }

    // ============================================================================
    // Extract(): step 1, user corrections — do-not-save sentences must be skipped even
    // when they also read as guidance.
    // ============================================================================

    [Fact] // Negating or removing the "continue" would let a do-not-save sentence that also
           // contains a prescriptive verb ("always use ...") slip through as a candidate.
    public void Extract_UserCorrection_DoNotSaveSentence_IsSkippedEvenWhenItAlsoReadsAsGuidance()
    {
        var result = Extractor().Extract(
            "Do not save this, but always use retryPolicy here", assistantText: null, maxCandidateCharacters: 500);
        Assert.Empty(result);
    }

    [Fact] // isGuidance = security || analyzer.IsCandidate: isolate the security-only operand
           // (analyzer alone would say "no") to catch an OR->AND mutation.
    public void Extract_UserCorrection_SecurityOnly_IsGuidance_EvenWithoutAnalyzerMatch()
    {
        var result = Extractor().Extract(
            "Validation happens at the boundary layer", assistantText: null, maxCandidateCharacters: 500);
        var candidate = Assert.Single(result);
        Assert.Equal(TurnCandidateSource.UserCorrection, candidate.Source);
        Assert.True(candidate.Security);
        Assert.Equal(100, candidate.Priority); // security always wins the priority ternary too.
    }

    [Fact] // Isolate the analyzer-only operand (no security signal present) for the same OR.
    public void Extract_UserCorrection_AnalyzerOnly_IsGuidance_EvenWithoutSecuritySignal()
    {
        var result = Extractor().Extract(
            "Always use dependency injection for testability", assistantText: null, maxCandidateCharacters: 500);
        var candidate = Assert.Single(result);
        Assert.False(candidate.Security);
    }

    [Fact] // Neither security nor analyzer match: no candidate at all.
    public void Extract_UserCorrection_PlainNonGuidanceSentence_ProducesNoCandidate()
    {
        var result = Extractor().Extract("The weather is nice today", assistantText: null, maxCandidateCharacters: 500);
        Assert.Empty(result);
    }

    // ============================================================================
    // Extract(): step 2, agent self-identified lessons.
    // ============================================================================

    [Fact] // !ContainsAny(...) || IsDoNotSave(...): isolate the first operand (no
           // self-identified phrase, not a do-not-save sentence) — must be skipped.
    public void Extract_AssistantText_WithoutSelfIdentifiedPhrase_ProducesNoCandidate()
    {
        var result = Extractor().Extract(
            userText: null,
            "This is a plain assistant statement without any special phrases at all",
            maxCandidateCharacters: 500);
        Assert.Empty(result);
    }

    [Fact] // A genuine self-identified, non-do-not-save sentence must produce a candidate.
    public void Extract_AssistantText_WithSelfIdentifiedPhrase_ProducesCandidate()
    {
        var result = Extractor().Extract(
            userText: null,
            "The lesson here is keep functions short and readable for humans",
            maxCandidateCharacters: 500);
        var candidate = Assert.Single(result);
        Assert.Equal(TurnCandidateSource.AgentSelfIdentified, candidate.Source);
        Assert.Equal("keep functions short and readable for humans", candidate.Text);
        Assert.False(candidate.Security);
        Assert.False(candidate.Performance);
        Assert.False(candidate.Conditional);
        Assert.False(candidate.HasSymbol);
        Assert.Equal(70, candidate.Priority);
    }

    [Fact] // !LooksSubstantive(lesson): a short stripped lesson must be skipped, not kept.
    public void Extract_AssistantText_NonSubstantiveLesson_IsSkipped()
    {
        var result = Extractor().Extract(userText: null, "The lesson here is ok", maxCandidateCharacters: 500);
        Assert.Empty(result);
    }

    // ---- Build(): conditional flag (When/If prefix, or " when " mid-sentence). ----

    [Fact]
    public void Extract_Conditional_True_WhenLessonStartsWithWhen()
    {
        var result = Extractor().Extract(
            userText: null,
            "The lesson here is when retries fail we should log the error clearly",
            maxCandidateCharacters: 500);
        Assert.True(Assert.Single(result).Conditional);
    }

    [Fact] // Isolates the "if " clause: without it, an OR-collapsed-to-AND mutation would
           // require the "when " clause to also be true, which it isn't here.
    public void Extract_Conditional_True_WhenLessonStartsWithIf()
    {
        var result = Extractor().Extract(
            userText: null,
            "The lesson here is if retries fail always log the error clearly",
            maxCandidateCharacters: 500);
        Assert.True(Assert.Single(result).Conditional);
    }

    [Fact] // Isolates the " when " (mid-sentence) clause.
    public void Extract_Conditional_True_WhenLessonContainsWhenMidSentence()
    {
        var result = Extractor().Extract(
            userText: null,
            "The lesson here is retry only when errors are transient in nature",
            maxCandidateCharacters: 500);
        Assert.True(Assert.Single(result).Conditional);
    }

    // ---- Build(): HasSymbol (MemberOrCall || PascalIdentifier). ----

    [Fact] // Call syntax only ("Retry(") — no PascalCase word — isolates the first operand.
    public void Extract_HasSymbol_True_ViaCallSyntaxAlone()
    {
        var result = Extractor().Extract(
            userText: null,
            "The lesson here is invoke Retry() before returning",
            maxCandidateCharacters: 500);
        Assert.True(Assert.Single(result).HasSymbol);
    }

    [Fact] // A PascalCase/camelCase identifier only, no member access or call — isolates the
           // second operand.
    public void Extract_HasSymbol_True_ViaPascalIdentifierAlone()
    {
        var result = Extractor().Extract(
            userText: null,
            "The lesson here is update the userConfig object safely",
            maxCandidateCharacters: 500);
        Assert.True(Assert.Single(result).HasSymbol);
    }

    // ---- Build(): priority ternary (security > self-identified > conditional > performance > generic). ----

    [Fact]
    public void Extract_Priority_Conditional_UserCorrection_Is60()
    {
        var result = Extractor().Extract(
            "When errors occur, always retry the operation", assistantText: null, maxCandidateCharacters: 500);
        var candidate = Assert.Single(result);
        Assert.False(candidate.Security);
        Assert.True(candidate.Conditional);
        Assert.False(candidate.Performance);
        Assert.Equal(60, candidate.Priority);
    }

    [Fact]
    public void Extract_Priority_Performance_UserCorrection_Is40()
    {
        var result = Extractor().Extract(
            "Always pass the entity to the downstream method directly", assistantText: null, maxCandidateCharacters: 500);
        var candidate = Assert.Single(result);
        Assert.False(candidate.Security);
        Assert.False(candidate.Conditional);
        Assert.True(candidate.Performance);
        Assert.Equal(40, candidate.Priority);
    }

    [Fact]
    public void Extract_Priority_Generic_UserCorrection_Is20()
    {
        var result = Extractor().Extract(
            "Always format code consistently across the project", assistantText: null, maxCandidateCharacters: 500);
        var candidate = Assert.Single(result);
        Assert.False(candidate.Security);
        Assert.False(candidate.Conditional);
        Assert.False(candidate.Performance);
        Assert.Equal(20, candidate.Priority);
    }

    // ============================================================================
    // StripLeadIn: the colon-clause takes priority over the lead-in list.
    // ============================================================================

    [Fact] // Normal case: colon present with real content after it.
    public void Extract_StripLeadIn_PrefersTextAfterColon_WhenColonHasContent()
    {
        var result = Extractor().Extract(
            userText: null,
            "One worth storing is: use retries for network calls consistently",
            maxCandidateCharacters: 500);
        Assert.Equal("use retries for network calls consistently", Assert.Single(result).Text);
    }

    [Fact] // Edge case: colon is the very first character (index 0) — distinguishes
           // "colon >= 0" from a mutated "colon > 0".
    public void Extract_StripLeadIn_PrefersTextAfterColon_WhenColonIsFirstCharacter()
    {
        var result = Extractor().Extract(
            userText: null,
            ": convention is use retries for safety in every network call",
            maxCandidateCharacters: 500);
        Assert.Equal("convention is use retries for safety in every network call", Assert.Single(result).Text);
    }

    // ============================================================================
    // StripLeadIn: every documented lead-in phrase is actually recognised and stripped.
    // ============================================================================

    [Theory]
    [InlineData("One worth storing is keep retries idempotent and safe")]
    [InlineData("Worth storing is keep retries idempotent and safe")]
    [InlineData("This is worth storing keep retries idempotent and safe")]
    [InlineData("One thing worth saving is keep retries idempotent and safe")]
    [InlineData("One worth saving is keep retries idempotent and safe")]
    [InlineData("Worth saving is keep retries idempotent and safe")]
    [InlineData("One reusable lesson is keep retries idempotent and safe")]
    [InlineData("A reusable lesson is keep retries idempotent and safe")]
    [InlineData("The reusable lesson is keep retries idempotent and safe")]
    [InlineData("The reusable lesson here is keep retries idempotent and safe")]
    [InlineData("Reusable lesson is keep retries idempotent and safe")]
    [InlineData("The lesson here is keep retries idempotent and safe")]
    [InlineData("The lesson is keep retries idempotent and safe")]
    [InlineData("The convention is keep retries idempotent and safe")]
    public void Extract_StripLeadIn_StripsEveryDocumentedLeadIn(string assistantSentence)
    {
        var result = Extractor().Extract(userText: null, assistantSentence, maxCandidateCharacters: 500);
        Assert.Equal("keep retries idempotent and safe", Assert.Single(result).Text);
    }

    // ============================================================================
    // LooksSubstantive: words.Length >= 4 && text.Length >= 16 boundaries.
    // ============================================================================

    [Fact] // Exactly at both boundaries (4 words, 16 characters) — must be substantive.
    public void Extract_LooksSubstantive_True_AtExactWordAndLengthBoundary()
    {
        var result = Extractor().Extract(userText: null, "The lesson is abc def ghi jklm", maxCandidateCharacters: 500);
        Assert.Equal("abc def ghi jklm", Assert.Single(result).Text);
    }

    [Fact] // One word under the boundary (3 words), even though length is well over 16.
    public void Extract_LooksSubstantive_False_OneWordUnderBoundary()
    {
        var result = Extractor().Extract(userText: null, "The lesson is abcdef ghijkl mnopqr", maxCandidateCharacters: 500);
        Assert.Empty(result);
    }

    [Fact] // Enough words (4), but one character under the length boundary (15).
    public void Extract_LooksSubstantive_False_OneCharacterUnderLengthBoundary()
    {
        var result = Extractor().Extract(userText: null, "The lesson is abc def ghi jkl", maxCandidateCharacters: 500);
        Assert.Empty(result);
    }

    // ============================================================================
    // Clamp: maxCandidateCharacters <= 0 means "no clamp"; otherwise truncate with "…".
    // ============================================================================

    [Fact] // maxCharacters == 0 is the "do not clamp" sentinel, not "clamp to nothing".
    public void Extract_Clamp_NoTruncation_WhenMaxCharactersIsZero()
    {
        var result = Extractor().Extract(userText: null, "The lesson is abc def ghi jklm", maxCandidateCharacters: 0);
        Assert.Equal("abc def ghi jklm", Assert.Single(result).Text);
    }

    [Fact] // Exact boundary: trimmed.Length == maxCharacters must NOT truncate.
    public void Extract_Clamp_NoTruncation_WhenLengthEqualsMaxCharacters()
    {
        var result = Extractor().Extract(userText: null, "The lesson is abc def ghi jklm", maxCandidateCharacters: 16);
        Assert.Equal("abc def ghi jklm", Assert.Single(result).Text);
    }

    [Fact] // Below the boundary: truncates to exactly maxCharacters and appends the "…" marker.
    public void Extract_Clamp_TruncatesAndAppendsEllipsis_WhenOverMaxCharacters()
    {
        var result = Extractor().Extract(userText: null, "The lesson is abc def ghi jklm", maxCandidateCharacters: 10);
        Assert.Equal("abc def gh…", Assert.Single(result).Text);
    }

    // ============================================================================
    // SplitSentences: null input must never throw and must yield no candidates.
    // ============================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Extract_NullOrWhitespaceText_NeverThrows_AndProducesNoCandidates(string? text)
    {
        var result = Extractor().Extract(text, text, maxCandidateCharacters: 500);
        Assert.Empty(result);
    }
}
