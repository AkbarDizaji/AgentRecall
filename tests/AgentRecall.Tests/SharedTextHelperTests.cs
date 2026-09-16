using AgentRecall.Core.Text;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// The shared text helpers now sit under every similarity check, every stored id list and
/// every one-line label in the CLI, so their edge cases are pinned here rather than
/// rediscovered separately by each caller.
/// </summary>
public class SharedTextHelperTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("...", "")]
    [InlineData("Use IsEventsFeatureEnabled!", "use iseventsfeatureenabled")]
    [InlineData("code_review  and\tauth-token", "code review and auth token")]
    public void Collapse_folds_text_to_comparable_words(string? input, string expected) =>
        Assert.Equal(expected, TextNormalization.Collapse(input));

    [Fact]
    public void Collapse_makes_repunctuated_text_compare_equal() =>
        Assert.Equal(
            TextNormalization.Collapse("Prefer the canonical gate."),
            TextNormalization.Collapse("prefer  THE canonical  gate!!"));

    [Fact]
    public void SubjectTokens_drops_short_and_noise_words()
    {
        var noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "when", "the" };

        Assert.Equal(
            ["feature", "gates", "implementing"],
            TextNormalization.SubjectTokens("When implementing the feature gates, a b c", noise).Order());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,")]
    public void Parse_reads_no_ids_from_empty_input(string? csv) =>
        Assert.Empty(IdList.Parse(csv));

    [Fact]
    public void Parse_keeps_order_and_skips_unparseable_entries() =>
        Assert.Equal([21, 3, 25], IdList.Parse(" 21 ,, abc, 3,25"));

    [Fact]
    public void Parse_round_trips_what_Join_wrote() =>
        Assert.Equal([4, 8, 15], IdList.Parse(IdList.Join([4, 8, 15])));

    [Theory]
    [InlineData(null, 10, "")]
    [InlineData("rule", 0, "")]
    [InlineData("rule", -1, "")]
    [InlineData("rule", 10, "rule")]
    [InlineData("rule", 4, "rule")]
    [InlineData("preserve else semantics", 8, "preserv…")]
    public void Ellipsize_never_exceeds_the_cap(string? value, int max, string expected)
    {
        var actual = TextTruncation.Ellipsize(value, max);

        Assert.Equal(expected, actual);
        Assert.True(actual.Length <= Math.Max(max, 0));
    }

    [Theory]
    [InlineData(0, "rules")]
    [InlineData(1, "rule")]
    [InlineData(2, "rules")]
    public void Of_agrees_with_the_count(int count, string expected) =>
        Assert.Equal(expected, Plural.Of(count, "rule"));
}
