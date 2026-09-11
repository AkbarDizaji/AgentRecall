using AgentRecall.Cli.ClaudeCode;
using AgentRecall.Core;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// The guidance is a document compiled in as a resource rather than a string literal, so the ways
/// it can break are new: a resource that fails to embed, a placeholder nobody substituted, or a
/// heading that drifts from the constant used to find the block for an in-place refresh. Each of
/// those would be silent — a project would simply receive the wrong instructions.
/// </summary>
public class ClaudeMdGuidanceTests
{
    [Fact]
    public void Text_LoadsTheEmbeddedDocument()
    {
        var text = ClaudeMdGuidance.Text;

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.True(text.Split('\n').Length > 400, "the guidance document should be the full block.");
    }

    // The heading is how an existing block is located and replaced. If the document stopped opening
    // with it, Ensure would append a second block instead of refreshing the first.
    [Fact]
    public void Text_OpensWithTheHeadingUsedToFindTheBlock()
    {
        Assert.StartsWith(ClaudeMdGuidance.Heading, ClaudeMdGuidance.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_SubstitutesEveryPlaceholder()
    {
        var text = ClaudeMdGuidance.Text;

        Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
        Assert.Equal(AgentContract.Version, AgentContract.ReadDeclaredVersion(text));
    }

    // Line endings decide whether an up-to-date block compares equal to the one on disk. A CRLF
    // checkout that slipped through would make every Ensure report an update and rewrite the file.
    [Fact]
    public void Text_UsesLineFeedsOnly()
    {
        Assert.DoesNotContain('\r', ClaudeMdGuidance.Text);
    }

    [Fact]
    public void Ensure_IsIdempotentAcrossRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentrecall-guidance-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(GuidanceOutcome.Created, ClaudeMdGuidance.Ensure(root));

            var written = File.ReadAllText(Path.Combine(root, ClaudeMdGuidance.RelativePath));
            Assert.Equal(ClaudeMdGuidance.Text, written);

            Assert.Equal(GuidanceOutcome.AlreadyPresent, ClaudeMdGuidance.Ensure(root));
            Assert.Equal(written, File.ReadAllText(Path.Combine(root, ClaudeMdGuidance.RelativePath)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
