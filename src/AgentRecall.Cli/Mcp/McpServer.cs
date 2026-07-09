using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecall.Cli.Mcp.Tools;
using AgentRecall.Core;
using AgentRecall.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentRecall.Cli.Mcp;

/// <summary>
/// A minimal Model Context Protocol server speaking JSON-RPC 2.0 over stdio
/// (newline-delimited messages). Handles initialize, tools/list, tools/call and
/// ping. Designed for Claude Code.
/// </summary>
public sealed class McpServer
{
    private const string DefaultProtocolVersion = "2024-11-05";

    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly Dictionary<string, IMcpTool> _tools;

    public McpServer(IServiceProvider services, IEnumerable<IMcpTool>? tools = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("agentrecall.mcp");
        _tools = (tools ?? DefaultTools()).ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    /// <summary>The default tool set exposed by the server.</summary>
    public static IReadOnlyList<IMcpTool> DefaultTools() =>
    [
        new SearchRulesTool(),
        new GetRuleTool(),
        new AddFeedbackTool(),
        new GetProjectRulesTool(),
        new GetRelevantContextTool(),
        new SuggestFeedbackCandidateTool(),
        new CaptureFeedbackTool(),
        new GetRemindersTool(),
        new CaptureStatusTool(),
        new ResolveRulesTool(),
        new CompressMemoryTool(),
        new InjectContextTool(),
        new ImportPrCommentsTool(),
    ];

    /// <summary>The tools this server exposes, keyed by name.</summary>
    public IReadOnlyDictionary<string, IMcpTool> Tools => _tools;

    /// <summary>
    /// Runs the read/dispatch/write loop until <paramref name="input"/> reaches
    /// end-of-stream or the token is cancelled.
    /// </summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        // Ensure the database exists before serving requests.
        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                .InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("AgentRecall MCP server started with {Count} tools.", _tools.Count);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break; // EOF
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var response = await HandleMessageAsync(line, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                await WriteMessageAsync(output, response).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Parses and dispatches a single JSON-RPC message. Returns the response
    /// node, or null for notifications (and parse errors with no id).
    /// </summary>
    public async Task<JsonNode?> HandleMessageAsync(string message, CancellationToken cancellationToken)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(message);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }

        if (root is not JsonObject request)
        {
            return Error(null, -32600, "Invalid Request");
        }

        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();
        var isNotification = !request.ContainsKey("id");

        if (string.IsNullOrEmpty(method))
        {
            return isNotification ? null : Error(id, -32600, "Invalid Request");
        }

        try
        {
            switch (method)
            {
                case "initialize":
                    return Result(id, Initialize(request["params"] as JsonObject));

                case "notifications/initialized":
                case "notifications/cancelled":
                    return null; // notifications: no response

                case "ping":
                    return Result(id, new JsonObject());

                case "tools/list":
                    return Result(id, ListTools());

                case "tools/call":
                    return await CallToolAsync(id, request["params"] as JsonObject, cancellationToken).ConfigureAwait(false);

                default:
                    return isNotification ? null : Error(id, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling method {Method}.", method);
            return isNotification ? null : Error(id, -32603, $"Internal error: {ex.Message}");
        }
    }

    private JsonObject Initialize(JsonObject? @params)
    {
        var protocolVersion = @params?["protocolVersion"]?.GetValue<string>() ?? DefaultProtocolVersion;

        return new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = AppInfo.Name,
                ["version"] = AppInfo.Version,
            },
        };
    }

    private JsonObject ListTools()
    {
        var tools = new JsonArray();
        foreach (var tool in _tools.Values)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }

        return new JsonObject { ["tools"] = tools };
    }

    private async Task<JsonNode?> CallToolAsync(JsonNode? id, JsonObject? @params, CancellationToken cancellationToken)
    {
        var name = @params?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name) || !_tools.TryGetValue(name, out var tool))
        {
            return Error(id, -32602, $"Unknown tool: {name}");
        }

        var arguments = @params?["arguments"] as JsonObject;

        try
        {
            await using var scope = _services.CreateAsyncScope();
            var payload = await tool.InvokeAsync(arguments, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);

            return Result(id, ToolContent(payload, isError: false));
        }
        catch (ArgumentException ex)
        {
            // Invalid input from the caller — report as a tool error, not a protocol error.
            return Result(id, ToolContent(new JsonObject { ["error"] = ex.Message }, isError: true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any other tool failure is contained as a tool-level error (isError:true)
            // rather than a JSON-RPC internal error, so one tool throwing never corrupts
            // the protocol stream. The full exception goes to stderr (stdout is the
            // protocol channel) for diagnosis.
            _logger.LogError(ex, "MCP tool {Tool} failed.", name);
            Console.Error.WriteLine($"[agentrecall] MCP tool '{name}' failed: {ex}");
            return Result(id, ToolContent(new JsonObject { ["error"] = $"Tool '{name}' failed: {ex.Message}" }, isError: true));
        }
    }

    private static JsonObject ToolContent(JsonNode payload, bool isError)
    {
        var text = payload.ToJsonString(McpJson.Indented);
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text },
            },
            ["structuredContent"] = payload,
            ["isError"] = isError,
        };
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string messageText) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = messageText },
    };

    private static async Task WriteMessageAsync(TextWriter output, JsonNode message)
    {
        // One compact JSON object per line; never embed newlines.
        await output.WriteAsync(message.ToJsonString(McpJson.Options)).ConfigureAwait(false);
        await output.WriteAsync('\n').ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }
}
