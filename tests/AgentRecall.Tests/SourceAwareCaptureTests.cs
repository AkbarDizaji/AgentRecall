using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AgentRecall.Core.Finalization;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for source/outcome-aware capture: the deterministic, English-only classifier that
/// labels a candidate's source (documentation, tool/skill instruction, command output, log
/// line, assistant meta-prose, user/review feedback, explicit save/do-not-save) and the
/// decision matrix that keeps read-only source material out of memory unless it is paired
/// with an observed failure, an explicit save, or a confirmed repository convention.
///
/// The classifier prefers structured origin metadata and falls back to compiled,
/// timeout-guarded regex pattern groups. It is offline and deterministic — no DB needed.
/// </summary>
public class SourceAwareCaptureTests
{
    private static CandidateSourceKind Kind(string text, CandidateOrigin origin = CandidateOrigin.Unknown) =>
        CandidateSourceClassifier.Classify(text, origin).Kind;

    private static SourceCaptureVerdict DecideUnpaired(string text, CandidateOrigin origin = CandidateOrigin.Unknown) =>
        SourceAwareCaptureDecision.Decide(
            CandidateSourceClassifier.Classify(text, origin),
            pairedWithObservedFailure: false,
            pairedWithExplicitSave: false,
            pairedWithRepositoryConfirmation: false);

    // ---- Instruction-shaped source text classifies as source/tool/doc-only --------

    // These four are the reported examples. They classify as read-only source material from
    // their *shape* (a flag, an ALL_CAPS placeholder, a command, a log/operational note),
    // not because any exact sentence is hardcoded.
    [Theory]
    [InlineData("Use --verbosity normal so pass/fail status is visible.", CandidateSourceKind.ToolOrSkillInstruction)]
    [InlineData("Use git status --porcelain or the user-provided list of files/test names.", CandidateSourceKind.CommandOutput)]
    [InlineData("Use the created directory path as RESULTS_DIR for subsequent steps.", CandidateSourceKind.ToolOrSkillInstruction)]
    [InlineData("A leftover process from a previous run will cause port conflicts.", CandidateSourceKind.LogOutput)]
    public void InstructionShapedText_ClassifiesAsReadOnlySource(string text, CandidateSourceKind expected)
    {
        Assert.Equal(expected, Kind(text));
        // And on its own — no pairing — it is skipped.
        Assert.True(DecideUnpaired(text).ShouldSkip);
    }

    // The classifier only reaches a source/tool/doc kind when a shape or metadata says so; a
    // plain user sentence with none of those is never mislabelled as source material.
    [Theory]
    [InlineData("Prefer composition over inheritance for the service layer.")]
    [InlineData("Keep the handler small and delegate the parsing to a helper.")]
    public void PlainGuidance_IsNotReadOnlySource(string text)
    {
        var kind = Kind(text);
        Assert.DoesNotContain(kind, new[]
        {
            CandidateSourceKind.SourceDocumentInstruction,
            CandidateSourceKind.ToolOrSkillInstruction,
            CandidateSourceKind.CommandOutput,
            CandidateSourceKind.LogOutput,
        });
        Assert.False(DecideUnpaired(text).ShouldSkip);
    }

    // ---- Structured metadata is trusted before regex --------------------------------

    [Theory]
    [InlineData(CandidateOrigin.SkillDoc, CandidateSourceKind.SourceDocumentInstruction)]
    [InlineData(CandidateOrigin.ToolDoc, CandidateSourceKind.ToolOrSkillInstruction)]
    [InlineData(CandidateOrigin.CommandOutput, CandidateSourceKind.CommandOutput)]
    [InlineData(CandidateOrigin.LogOutput, CandidateSourceKind.LogOutput)]
    public void StructuredOrigin_WinsOverText(CandidateOrigin origin, CandidateSourceKind expected)
    {
        // Even text that would otherwise look like a user correction is trusted as source
        // material when the host's metadata says where it came from.
        Assert.Equal(expected, Kind("use the loaded entity instead of re-querying it", origin));
    }

    // ---- Per-kind skip behaviour (the decision matrix) ------------------------------

    [Fact]
    public void SourceDocumentOnly_Skips()
    {
        var verdict = DecideUnpaired("use the loaded entity", CandidateOrigin.SkillDoc);
        Assert.True(verdict.ShouldSkip);
        Assert.Equal(CaptureSkipReason.SourceDocument, verdict.SkipReason);
    }

    [Fact]
    public void ToolOrSkillInstructionOnly_Skips()
    {
        var verdict = DecideUnpaired("Use the created directory path as RESULTS_DIR for subsequent steps.");
        Assert.True(verdict.ShouldSkip);
        Assert.Equal(CaptureSkipReason.ToolOrSkillInstruction, verdict.SkipReason);
    }

    [Fact]
    public void CommandOutputOnly_Skips()
    {
        var verdict = DecideUnpaired("Run git status --porcelain to list changed files.");
        Assert.True(verdict.ShouldSkip);
        Assert.Equal(CaptureSkipReason.CommandOutput, verdict.SkipReason);
    }

    [Fact]
    public void LogOutputOnly_Skips()
    {
        var verdict = DecideUnpaired("ERROR failed to bind to port 5000, address already in use.");
        Assert.True(verdict.ShouldSkip);
        Assert.Equal(CaptureSkipReason.LogOutput, verdict.SkipReason);
    }

    [Fact]
    public void AssistantMetaProseOnly_Skips()
    {
        var verdict = DecideUnpaired("One thing is worth saving here, want me to add it?", CandidateOrigin.AssistantMessage);
        Assert.True(verdict.ShouldSkip);
        Assert.Equal(CaptureSkipReason.AssistantProse, verdict.SkipReason);
    }

    // ---- Read-only source is allowed only when paired -------------------------------

    [Fact]
    public void DocBackedByObservedFailure_IsAllowed()
    {
        var classification = CandidateSourceClassifier.Classify(
            "Use the created directory path as RESULTS_DIR for subsequent steps.", CandidateOrigin.ToolDoc);

        var verdict = SourceAwareCaptureDecision.Decide(
            classification,
            pairedWithObservedFailure: true,
            pairedWithExplicitSave: false,
            pairedWithRepositoryConfirmation: false);

        Assert.False(verdict.ShouldSkip);
    }

    [Fact]
    public void DocBackedByExplicitSave_IsAllowed()
    {
        var classification = CandidateSourceClassifier.Classify("skill says run the setup step", CandidateOrigin.SkillDoc);

        var verdict = SourceAwareCaptureDecision.Decide(
            classification,
            pairedWithObservedFailure: false,
            pairedWithExplicitSave: true,
            pairedWithRepositoryConfirmation: false);

        Assert.False(verdict.ShouldSkip);
    }

    [Fact]
    public void DocBackedByRepositoryConfirmation_IsAllowed()
    {
        var classification = CandidateSourceClassifier.Classify("tool doc instruction", CandidateOrigin.ToolDoc);

        var verdict = SourceAwareCaptureDecision.Decide(
            classification,
            pairedWithObservedFailure: false,
            pairedWithExplicitSave: false,
            pairedWithRepositoryConfirmation: true);

        Assert.False(verdict.ShouldSkip);
    }

    // Every read-only kind must actually reach the quality gate when paired — not just
    // SourceDocument/ToolOrSkill (already covered above by the Doc* tests). Without a
    // dedicated CommandOutput/LogOutput paired case, a mutant that hard-codes those two
    // branches to always skip would go unnoticed.
    [Fact]
    public void CommandOutputBackedByObservedFailure_IsAllowed()
    {
        var classification = CandidateSourceClassifier.Classify(
            "Run git status --porcelain to list changed files.", CandidateOrigin.CommandOutput);

        var verdict = SourceAwareCaptureDecision.Decide(
            classification, pairedWithObservedFailure: true, pairedWithExplicitSave: false, pairedWithRepositoryConfirmation: false);

        Assert.False(verdict.ShouldSkip);
    }

    [Fact]
    public void LogOutputBackedByRepositoryConfirmation_IsAllowed()
    {
        var classification = CandidateSourceClassifier.Classify(
            "ERROR failed to bind to port 5000, address already in use.", CandidateOrigin.LogOutput);

        var verdict = SourceAwareCaptureDecision.Decide(
            classification, pairedWithObservedFailure: false, pairedWithExplicitSave: false, pairedWithRepositoryConfirmation: true);

        Assert.False(verdict.ShouldSkip);
    }

    // Assistant meta-prose only earns its way in on an explicit save OR an observed failure —
    // a repository confirmation alone is NOT enough (unlike the other read-only kinds above).
    [Fact]
    public void AssistantMetaProse_RepositoryConfirmationAlone_StillSkips()
    {
        var verdict = SourceAwareCaptureDecision.Decide(
            CandidateSourceClassifier.Classify("One thing is worth saving here, want me to add it?", CandidateOrigin.AssistantMessage),
            pairedWithObservedFailure: false, pairedWithExplicitSave: false, pairedWithRepositoryConfirmation: true);

        Assert.True(verdict.ShouldSkip);
    }

    [Fact]
    public void AssistantMetaProseBackedByObservedFailure_IsAllowed()
    {
        var verdict = SourceAwareCaptureDecision.Decide(
            CandidateSourceClassifier.Classify("One thing is worth saving here, want me to add it?", CandidateOrigin.AssistantMessage),
            pairedWithObservedFailure: true, pairedWithExplicitSave: false, pairedWithRepositoryConfirmation: false);

        Assert.False(verdict.ShouldSkip);
    }

    [Fact]
    public void AssistantMetaProseBackedByExplicitSave_IsAllowed()
    {
        var verdict = SourceAwareCaptureDecision.Decide(
            CandidateSourceClassifier.Classify("One thing is worth saving here, want me to add it?", CandidateOrigin.AssistantMessage),
            pairedWithObservedFailure: false, pairedWithExplicitSave: true, pairedWithRepositoryConfirmation: false);

        Assert.False(verdict.ShouldSkip);
    }

    // ---- Explicit save / do-not-save ------------------------------------------------

    [Fact]
    public void ExplicitSave_IsClassifiedAndAllowed()
    {
        Assert.Equal(CandidateSourceKind.UserExplicitSave,
            Kind("Save this: use stdout only for JSON and stderr for status messages.", CandidateOrigin.UserMessage));
        Assert.False(DecideUnpaired("Save this: use stdout only for JSON.", CandidateOrigin.UserMessage).ShouldSkip);
    }

    [Fact]
    public void ExplicitDoNotSave_HardSkips_EvenWhenPaired()
    {
        var classification = CandidateSourceClassifier.Classify("do not save this", CandidateOrigin.UserMessage);
        Assert.Equal(CandidateSourceKind.UserExplicitDoNotSave, classification.Kind);

        // Even with every pairing signal on, a do-not-save is a hard skip.
        var verdict = SourceAwareCaptureDecision.Decide(
            classification,
            pairedWithObservedFailure: true,
            pairedWithExplicitSave: true,
            pairedWithRepositoryConfirmation: true);

        Assert.True(verdict.ShouldSkip);
        Assert.Equal(CaptureSkipReason.ExplicitDoNotSave, verdict.SkipReason);
    }

    // ---- False-positive protection (the reported cases A–D) -------------------------

    // A. User technical guidance that starts with "Use" is NOT source-document-only.
    [Fact]
    public void FalsePositive_A_UserGuidanceStartingWithUse_IsNotSourceDoc()
    {
        var kind = Kind(
            "Use the existing payment attach path instead of writing duplicate Stripe attach logic.",
            CandidateOrigin.UserMessage);

        Assert.Equal(CandidateSourceKind.UserFeedback, kind);
        Assert.False(DecideUnpaired(
            "Use the existing payment attach path instead of writing duplicate Stripe attach logic.",
            CandidateOrigin.UserMessage).ShouldSkip);
    }

    // B. Reviewer correction classifies as review feedback (capturable), not source-doc.
    [Fact]
    public void FalsePositive_B_ReviewerCorrection_IsReviewFeedback()
    {
        var kind = Kind("Use the loaded entity instead of re-querying it.", CandidateOrigin.ReviewComment);

        Assert.Equal(CandidateSourceKind.ReviewFeedback, kind);
        Assert.False(DecideUnpaired("Use the loaded entity instead of re-querying it.", CandidateOrigin.ReviewComment).ShouldSkip);
    }

    // C. An explicit save request is honoured even though its body is instruction-shaped.
    [Fact]
    public void FalsePositive_C_ExplicitSave_IsHonoured()
    {
        Assert.Equal(CandidateSourceKind.UserExplicitSave,
            Kind("Save this: use stdout only for JSON and stderr for status messages.", CandidateOrigin.UserMessage));
    }

    // D. The same instruction, read from a tool doc, is tool/skill-only and skipped.
    [Fact]
    public void FalsePositive_D_ToolDocInstruction_IsSkipped()
    {
        var verdict = DecideUnpaired(
            "Use the created directory path as RESULTS_DIR for subsequent steps.", CandidateOrigin.ToolDoc);

        Assert.True(verdict.ShouldSkip);
        Assert.Equal(CaptureSkipReason.ToolOrSkillInstruction, verdict.SkipReason);
    }

    // ---- Every named pattern group matches its own shape ----------------------------

    [Fact]
    public void EachPatternGroup_MatchesItsShape()
    {
        Assert.Matches(CandidateSourceClassifier.ExplicitDoNotSaveIntentPattern, "please do not save this");
        Assert.Matches(CandidateSourceClassifier.ExplicitSaveIntentPattern, "please save this");
        Assert.Matches(CandidateSourceClassifier.ObservedFailureOrCorrectionPattern, "use X instead of Y");
        Assert.Matches(CandidateSourceClassifier.RepositoryConfirmationPattern, "in this repository we scope by tenant");
        Assert.Matches(CandidateSourceClassifier.AssistantMetaProsePattern, "want me to save it?");
        Assert.Matches(CandidateSourceClassifier.CommandOutputPattern, "git status --porcelain");
        Assert.Matches(CandidateSourceClassifier.LogOutputPattern, "ERROR could not connect");
        Assert.Matches(CandidateSourceClassifier.ToolOrSkillInstructionPattern, "pass --verbosity normal");
        Assert.Matches(CandidateSourceClassifier.SourceDocumentInstructionPattern, "see the section above");
    }

    // The uppercase log-level guard does not fire on lowercase prose (keeps false positives low).
    [Fact]
    public void LogPattern_DoesNotMatchLowercaseProse()
    {
        Assert.DoesNotMatch(CandidateSourceClassifier.LogOutputPattern, "handle the error and inform the user");
    }

    // The ALL_CAPS placeholder guard requires an underscore, so real acronyms are not placeholders.
    [Fact]
    public void ToolPattern_DoesNotMatchPlainAcronyms()
    {
        Assert.DoesNotMatch(CandidateSourceClassifier.ToolOrSkillInstructionPattern, "keep JSON on stdout and SQL parameterised");
    }

    // ---- Regex design constraints: every pattern is compiled and timeout-guarded ----

    [Fact]
    public void EveryPatternGroup_IsCompiledAndTimeoutGuarded()
    {
        var patterns = typeof(CandidateSourceClassifier)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(Regex))
            .Select(f => (Regex)f.GetValue(null)!)
            .ToList();

        // All nine named pattern groups are exposed.
        Assert.Equal(9, patterns.Count);
        Assert.All(patterns, p =>
        {
            Assert.True(p.MatchTimeout > TimeSpan.Zero, "regex must have a finite match timeout");
            Assert.NotEqual(Regex.InfiniteMatchTimeout, p.MatchTimeout);
            Assert.True(p.Options.HasFlag(RegexOptions.Compiled), "regex must be compiled");
        });
    }

    // Totality: the classifier returns a verdict for any input and never throws.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---- $$$$ ///")]
    public void Classify_NeverThrows_ForDegenerateInput(string? text)
    {
        var classification = CandidateSourceClassifier.Classify(text);
        Assert.False(string.IsNullOrEmpty(classification.Reason));
    }

    // ---- Mutation-hardening: every alternative in every pattern group must be load-bearing --

    // Each InlineData below isolates exactly one alternative of its pattern group — it contains
    // no phrase that would satisfy any *other* alternative in the same group, nor any pattern
    // checked earlier in Classify's evidence order. If Stryker deletes that one alternative, the
    // whole classification changes and these tests fail.

    [Fact]
    public void ExplicitDoNotSave_NotWorthSavingClause_IsRecognised()
    {
        var classification = CandidateSourceClassifier.Classify("This detail is not worth saving as a rule.");
        Assert.Equal(CandidateSourceKind.UserExplicitDoNotSave, classification.Kind);
        Assert.Equal("explicit do-not-save intent", classification.Reason);
    }

    [Fact]
    public void ExplicitSave_Reason_IsExact()
    {
        var classification = CandidateSourceClassifier.Classify("Save this for later.");
        Assert.Equal(CandidateSourceKind.UserExplicitSave, classification.Kind);
        Assert.Equal("explicit save intent", classification.Reason);
    }

    [Theory]
    [InlineData("You broke the tests with that change.")]
    [InlineData("This change introduced a regression in the parser.")]
    [InlineData("This changed the behavior unexpectedly.")]
    [InlineData("No, preserve the original ordering.")]
    [InlineData("You should have used the cached client.")]
    public void ObservedFailureOrCorrection_EachClause_IsRecognised(string text)
    {
        var classification = CandidateSourceClassifier.Classify(text);
        Assert.Equal(CandidateSourceKind.ObservedAgentFailure, classification.Kind);
        Assert.Equal("observed failure or correction", classification.Reason);
    }

    // These reach the Classify() repository-confirmation branch directly (unlike the
    // MatchesRepositoryConfirmation-only and direct-regex coverage elsewhere in this file), so
    // they also cover that branch's own reason string and kind — previously zero-coverage.
    [Theory]
    [InlineData("Use the existing helper for validation.")]
    [InlineData("That's our convention for handling retries.")]
    [InlineData("Follow the documented repository convention for migrations.")]
    [InlineData("We always validate the payload before persisting it.")]
    public void RepositoryConfirmation_EachClause_IsRecognisedByClassify(string text)
    {
        var classification = CandidateSourceClassifier.Classify(text);
        Assert.Equal(CandidateSourceKind.RepositoryConventionConfirmation, classification.Kind);
        Assert.Equal("repository-convention confirmation", classification.Reason);
    }

    [Theory]
    [InlineData("I didn't explicitly call the memory tool for this.")]
    [InlineData("The Stop hook may have captured this already.")]
    [InlineData("Here's what I'd save from this conversation.")]
    public void AssistantMetaProse_EachClause_IsRecognised(string text)
    {
        var classification = CandidateSourceClassifier.Classify(text);
        Assert.Equal(CandidateSourceKind.AssistantMetaProse, classification.Kind);
        Assert.Equal("assistant meta-prose shape", classification.Reason);
    }

    [Fact]
    public void CommandOutput_DollarPromptClause_IsRecognised()
    {
        var classification = CandidateSourceClassifier.Classify("$ ls -la");
        Assert.Equal(CandidateSourceKind.CommandOutput, classification.Kind);
        Assert.Equal("command-output shape", classification.Reason);
    }

    [Theory]
    [InlineData("Log entry at 2024-05-01 12:30 shows nothing unusual.")]
    [InlineData("There is a leftover config file to clean up.")]
    [InlineData("Running both processes will cause a naming conflict.")]
    public void LogOutput_EachClause_IsRecognised(string text)
    {
        var classification = CandidateSourceClassifier.Classify(text);
        Assert.Equal(CandidateSourceKind.LogOutput, classification.Kind);
        Assert.Equal("log-output shape", classification.Reason);
    }

    [Fact]
    public void ToolOrSkillInstruction_ForNextStepsClause_IsRecognised()
    {
        var classification = CandidateSourceClassifier.Classify("Copy the results for the next steps in the pipeline.");
        Assert.Equal(CandidateSourceKind.ToolOrSkillInstruction, classification.Kind);
        Assert.Equal("tool/skill instruction shape", classification.Reason);
    }

    // These reach the Classify() source-document branch directly, covering its own reason
    // string and kind — previously zero-coverage (the direct-regex test elsewhere in this file
    // only exercises the first alternative, and only via Regex.IsMatch, not via Classify()).
    [Theory]
    [InlineData("As documented above, the retry limit is five.")]
    [InlineData("Per the migration guide, run the setup script twice.")]
    public void SourceDocumentInstruction_EachClause_IsRecognisedByClassify(string text)
    {
        var classification = CandidateSourceClassifier.Classify(text);
        Assert.Equal(CandidateSourceKind.SourceDocumentInstruction, classification.Kind);
        Assert.Equal("source-document instruction shape", classification.Reason);
    }

    // The structured-metadata branch's reason strings are distinct per origin; a test that only
    // checks Kind (as StructuredOrigin_WinsOverText does) can't tell "structured metadata:
    // skill-doc" apart from an empty or Stryker-mutated string.
    [Theory]
    [InlineData(CandidateOrigin.SkillDoc, "structured metadata: skill-doc")]
    [InlineData(CandidateOrigin.ToolDoc, "structured metadata: tool-doc")]
    [InlineData(CandidateOrigin.CommandOutput, "structured metadata: command-output")]
    [InlineData(CandidateOrigin.LogOutput, "structured metadata: log-output")]
    public void StructuredOrigin_ReasonString_IsExact(CandidateOrigin origin, string expectedReason)
    {
        var classification = CandidateSourceClassifier.Classify("irrelevant body text", origin);
        Assert.Equal(expectedReason, classification.Reason);
    }

    // A null candidate must fall through the null-coalescing empty-string default, not some
    // other placeholder value, and be labelled exactly "empty candidate".
    [Fact]
    public void NullCandidate_IsEmptyCandidate_Exactly()
    {
        var classification = CandidateSourceClassifier.Classify(null);
        Assert.Equal(CandidateSourceKind.Unknown, classification.Kind);
        Assert.Equal("empty candidate", classification.Reason);
    }

    // ---- MatchesRepositoryConfirmation: direct coverage of the standalone helper ----

    // This helper is used by callers outside Classify() to test the pairing signal on its own;
    // it had zero direct coverage before this — every existing use of a repository-confirmation
    // string went through Classify() instead.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just a regular sentence")]
    public void MatchesRepositoryConfirmation_FalseCases(string? text)
    {
        Assert.False(CandidateSourceClassifier.MatchesRepositoryConfirmation(text));
    }

    [Fact]
    public void MatchesRepositoryConfirmation_TrueCase()
    {
        Assert.True(CandidateSourceClassifier.MatchesRepositoryConfirmation("in this repository we scope by tenant"));
    }

    // ---- Matches(): the fail-open timeout guard must return false, not true, on a timeout ----

    // Forcing a genuine RegexMatchTimeoutException without touching the source: build a
    // throwaway Regex with a vanishingly small timeout matched against a large input, then
    // invoke the classifier's private static timeout-guarded Matches(Regex, string) via
    // reflection — the only way to exercise the catch block, since none of the classifier's own
    // named patterns are pathological enough to time out within their real 250ms budget.
    [Fact]
    public void Matches_OnTimeout_FailsOpenToFalse()
    {
        var method = typeof(CandidateSourceClassifier).GetMethod(
            "Matches", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var slowPattern = new Regex(".*", RegexOptions.None, TimeSpan.FromTicks(1));
        var largeInput = new string('a', 2_000_000);

        var result = (bool)method!.Invoke(null, new object[] { slowPattern, largeInput })!;

        Assert.False(result);
    }
}
