using System.Text.Json.Nodes;

namespace AgentRecall.Cli.Mcp;

/// <summary>
/// A single MCP tool: its name, description, JSON-Schema for inputs, and an
/// invocation that runs against a request-scoped service provider.
/// </summary>
public interface IMcpTool
{
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema describing the tool's input arguments.</summary>
    JsonObject InputSchema { get; }

    /// <summary>
    /// Runs the tool. <paramref name="arguments"/> is the raw MCP arguments
    /// object (may be null). <paramref name="services"/> is a request scope.
    /// Returns the structured result payload.
    /// </summary>
    Task<JsonNode> InvokeAsync(
        JsonObject? arguments,
        IServiceProvider services,
        CancellationToken cancellationToken);
}

/// <summary>Helpers for reading values out of an MCP arguments object.</summary>
public static class McpArgs
{
    public static string? GetString(JsonObject? args, string key)
    {
        if (args is null || !args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        var value = node.GetValue<string?>();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string GetRequiredString(JsonObject? args, string key)
    {
        var value = GetString(args, key);
        if (value is null)
        {
            throw new ArgumentException($"Missing required argument '{key}'.");
        }

        return value;
    }

    public static int? GetInt(JsonObject? args, string key)
    {
        if (args is null || !args.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        // Accept both JSON numbers and numeric strings.
        if (node.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            return node.GetValue<int>();
        }

        return int.TryParse(node.GetValue<string?>(), out var parsed) ? parsed : null;
    }
}
