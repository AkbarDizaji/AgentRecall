using AgentRecall.Cli.Devcontainer;
using AgentRecall.Core;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// The contract is the seam between an installed CLI and the instructions driving it, and both
/// halves have to hold for drift to be detectable: the instructions must declare what they
/// expect, and the declaration must be machine-readable. These tests pin both, because the
/// failure they prevent is silent — an agent told to call tools its binary does not have.
/// </summary>
public class AgentContractTests
{
    [Fact]
    public void ReadDeclaredVersion_ReadsTheMarkerAsWritten()
    {
        Assert.Equal(AgentContract.Version, AgentContract.ReadDeclaredVersion(AgentContract.Marker));
        Assert.Equal(7, AgentContract.ReadDeclaredVersion("blah\n**AgentRecall contract: 7** — and then prose"));
    }

    // Instructions that predate stamping declare nothing, and "nothing" must not read as zero:
    // a bogus low number would make a current build look ahead of instructions it cannot satisfy.
    [Fact]
    public void ReadDeclaredVersion_WithoutADeclaration_IsNull()
    {
        Assert.Null(AgentContract.ReadDeclaredVersion(null));
        Assert.Null(AgentContract.ReadDeclaredVersion(""));
        Assert.Null(AgentContract.ReadDeclaredVersion("## Memory (AgentRecall)\n\nGuidance with no contract line."));
        Assert.Null(AgentContract.ReadDeclaredVersion("AgentRecall contract: soon"));
    }

    [Fact]
    public void Stamp_NamesTheRunningBuildAndItsContract()
    {
        Assert.Contains(AppInfo.Version, AgentContract.Stamp, StringComparison.Ordinal);
        Assert.Contains($"contract {AgentContract.Version}", AgentContract.Stamp, StringComparison.Ordinal);
    }

    // The guidance the scaffolder writes is the other half of the comparison. If it stops
    // declaring a contract, doctor has nothing to check and the drift goes back to being silent.
    [Fact]
    public void ScaffoldedGuidance_DeclaresTheCurrentContract()
    {
        Assert.Equal(
            AgentContract.Version,
            AgentContract.ReadDeclaredVersion(DevcontainerScaffolder.ClaudeMdGuidance));
    }
}
