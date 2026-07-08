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
}
