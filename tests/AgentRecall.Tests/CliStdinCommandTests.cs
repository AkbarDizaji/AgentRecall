using AgentRecall.Cli;
using AgentRecall.Cli.Mcp;
using AgentRecall.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// CLI commands that read the Claude Code payload from <see cref="Console.In"/>:
/// the finalize-turn / capture pipeline and the MCP stdio server loop. They redirect
/// the process-global <c>Console.In</c>, so they live in the serialized ConsoleStdin
/// collection and never run alongside each other or the other stdin tests.
/// </summary>
[Collection("ConsoleStdin")]
public class CliStdinCommandTests
{
    private static async Task<TestDatabase> NewDbAsync()
    {
        var db = new TestDatabase();
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        return db;
    }

    /// <summary>Runs a CLI command with <paramref name="stdin"/> piped in, capturing stdout.</summary>
    private static async Task<(int Code, string Output)> RunWithStdinAsync(
        TestDatabase db, string stdin, params string[] args)
    {
        var originalIn = Console.In;
        var writer = new StringWriter();
        try
        {
            Console.SetIn(new StringReader(stdin));
            var code = await CommandRouter.RunAsync(args, db.Services, writer);
            return (code, writer.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public async Task Hook_UnknownSubcommand_PrintsUsage()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunWithStdinAsync(db, string.Empty, "hook", "frobnicate");
        Assert.Equal(1, code);
        Assert.Contains("agentrecall hook", output);
    }

    [Fact]
    public async Task FinalizeTurn_MalformedPayload_ReportsNoLessons()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunWithStdinAsync(db, "not-json", "finalize-turn");
        Assert.Equal(0, code);
        Assert.Contains("No lessons found.", output);
    }

    [Fact]
    public async Task FinalizeTurn_MalformedPayload_Json_EmitsEmptyResult()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunWithStdinAsync(db, "not-json", "finalize-turn", "--json");
        Assert.Equal(0, code);
        Assert.Contains("captured", output);
        Assert.Contains("suggested", output);
    }

    [Fact]
    public async Task CaptureStatus_NoFinalizationYet_ReportsNone()
    {
        await using var db = await NewDbAsync();
        var (code, output) = await RunWithStdinAsync(db, string.Empty, "capture-status", "--last-turn");
        Assert.Equal(0, code);
        Assert.Contains("No finalization recorded yet.", output);
    }

    [Fact]
    public async Task Capture_EmptyPayload_NeverBlocks()
    {
        await using var db = await NewDbAsync();
        var (code, _) = await RunWithStdinAsync(db, string.Empty, "hook", "capture");
        Assert.Equal(0, code);
    }

    [Fact]
    public async Task Mcp_Command_RunsInitializeThenEof()
    {
        await using var db = await NewDbAsync();
        const string request =
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""";

        var (code, output) = await RunWithStdinAsync(db, request + "\n", "mcp");
        Assert.Equal(0, code);
        Assert.Contains("protocolVersion", output);
    }

    [Fact]
    public async Task McpServer_RunLoop_ProcessesMultipleMessagesUntilEof()
    {
        await using var db = await NewDbAsync();
        var server = new McpServer(db.Services);

        var input = new StringReader(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""" + "\n" +
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""" + "\n" +
            """{"jsonrpc":"2.0","id":3,"method":"ping"}""" + "\n");
        var output = new StringWriter();

        await server.RunAsync(input, output, CancellationToken.None);

        var text = output.ToString();
        Assert.Contains("protocolVersion", text);
        Assert.Contains("tools", text);
    }
}
