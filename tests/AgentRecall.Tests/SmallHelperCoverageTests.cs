using AgentRecall.Core.Domain;
using AgentRecall.Core.Mining;
using AgentRecall.Core.Services;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Small public helpers that were only exercised indirectly through the services that call
/// them: the log failure scanner, the mining clustering key, and the in-force/dead status
/// sets. Their contracts are pinned here so a change shows up against the helper itself.
/// </summary>
public class SmallHelperCoverageTests
{
    [Fact]
    public void Build_log_keeps_only_trimmed_error_lines()
    {
        var lines = new[] { "  error CS1002: ; expected  ", "Build succeeded.", "", "   ", "warning CS0168: unused" };

        Assert.Equal(["error CS1002: ; expected"], FailureLogParser.Parse(LogKind.Build, lines));
    }

    [Fact]
    public void Test_log_matches_failures_case_insensitively()
    {
        var lines = new[] { "Passed OrderTests.Refund", "FAILED OrderTests.Cancel", "Test run failed: 1 of 2" };

        Assert.Equal(
            ["FAILED OrderTests.Cancel", "Test run failed: 1 of 2"],
            FailureLogParser.Parse(LogKind.Test, lines));
    }

    [Fact]
    public void Lint_log_keeps_both_errors_and_warnings()
    {
        var lines = new[] { "src/a.ts:1 Warning: no-unused-vars", "src/b.ts:2 ERROR: eqeqeq", "2 files checked" };

        Assert.Equal(
            ["src/a.ts:1 Warning: no-unused-vars", "src/b.ts:2 ERROR: eqeqeq"],
            FailureLogParser.Parse(LogKind.Lint, lines));
    }

    [Fact]
    public void Unknown_log_kind_reports_no_failures() =>
        Assert.Empty(FailureLogParser.Parse((LogKind)99, ["error everywhere", "fail"]));

    [Fact]
    public void Parse_rejects_a_null_log() =>
        Assert.Throws<ArgumentNullException>(() => FailureLogParser.Parse(LogKind.Build, null!));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("please")]
    [InlineData("Could you just please")]
    public void NormalizeKey_is_empty_when_there_are_no_content_words(string? text) =>
        Assert.Equal(string.Empty, LessonTextNormalizer.NormalizeKey(text));

    [Theory]
    [InlineData("Don't use Moq.", "avoid moq")]
    [InlineData("never use   Moq!", "avoid moq")]
    [InlineData("Please avoid using Moq", "avoid moq")]
    [InlineData("Always use NSubstitute", "use nsubstitute")]
    [InlineData("You should use NSubstitute.", "use nsubstitute")]
    [InlineData("please please use NSubstitute", "use nsubstitute")]
    public void NormalizeKey_folds_equivalent_directives_onto_one_key(string text, string expected) =>
        Assert.Equal(expected, LessonTextNormalizer.NormalizeKey(text));

    [Theory]
    [InlineData("Always use Result<T>.", "use result<t>")]
    [InlineData("Could you just prefer It.IsAny<T>()", "use it.isany<t>()")]
    public void NormalizeKey_keeps_code_tokens_intact(string text, string expected) =>
        Assert.Equal(expected, LessonTextNormalizer.NormalizeKey(text));

    [Fact]
    public void NormalizeKey_is_deterministic() =>
        Assert.Equal(
            LessonTextNormalizer.NormalizeKey("Kindly don't use static mocks here"),
            LessonTextNormalizer.NormalizeKey("Kindly don't use static mocks here"));

    [Theory]
    [InlineData(RuleStatus.Active, true)]
    [InlineData(RuleStatus.Promoted, true)]
    [InlineData(RuleStatus.Draft, false)]
    [InlineData(RuleStatus.Pending, false)]
    [InlineData(RuleStatus.Superseded, false)]
    [InlineData(RuleStatus.Retired, false)]
    [InlineData(RuleStatus.Archived, false)]
    public void Only_active_and_promoted_rules_are_in_force(RuleStatus status, bool expected) =>
        Assert.Equal(expected, RuleStatusSets.IsEffective(new RecallRule { Status = status }));

    [Fact]
    public void A_deprecated_rule_is_never_in_force() =>
        Assert.False(RuleStatusSets.IsEffective(new RecallRule { Status = RuleStatus.Active, Deprecated = true }));

    [Fact]
    public void A_missing_rule_is_not_in_force() =>
        Assert.False(RuleStatusSets.IsEffective(null!));

    [Theory]
    [InlineData(RuleStatus.Superseded, true)]
    [InlineData(RuleStatus.Retired, true)]
    [InlineData(RuleStatus.Archived, true)]
    [InlineData(RuleStatus.Draft, false)]
    [InlineData(RuleStatus.Pending, false)]
    [InlineData(RuleStatus.Active, false)]
    [InlineData(RuleStatus.Promoted, false)]
    public void Only_superseded_retired_and_archived_are_dead(RuleStatus status, bool expected) =>
        Assert.Equal(expected, RuleStatusSets.IsInactive(status));

    [Fact]
    public void No_status_is_both_in_force_and_dead() =>
        Assert.Empty(RuleStatusSets.Effective.Intersect(RuleStatusSets.Inactive));
}
