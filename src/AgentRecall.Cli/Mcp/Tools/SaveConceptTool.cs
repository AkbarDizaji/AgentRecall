using System.Text.Json.Nodes;
using AgentRecall.Cli.Hooks;
using AgentRecall.Core.Concepts;
using AgentRecall.Core.Context;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: save what a business or technical term means here. Concepts are the knowledge code
/// cannot tell an agent — what a "venue migration" is, why an "event" waits for the commit — and
/// are delivered in their own section when a task names the term or reaches the code it lives in.
/// </summary>
public sealed class SaveConceptTool : IMcpTool
{
    public string Name => "save_concept";

    public string Description =>
        "Save a domain concept: what a business or technical term means in this project — knowledge " +
        "the code cannot tell you. Use it when the user explains a term, not for coding rules (those go " +
        "through submit_capture_judgment). Write the term and definition in English; put the names the " +
        "user uses in other languages in aliases (they are added to the glossary so prompts in that " +
        "language find the concept). Re-saving the same term updates it.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["term"] = Prop("string", "The term, in English (e.g. \"event\")."),
            ["definition"] = Prop("string", "What it means here, in one or two sentences."),
            ["aliases"] = StringArray("Other names for it, including in the user's language (e.g. \"ایونت\")."),
            ["paths"] = StringArray("Where it lives in the code: path patterns relative to the repository (e.g. \"src/Events\")."),
            ["symbols"] = StringArray("Classes or other symbols that implement it (e.g. \"EventPublisher\")."),
            ["global"] = Prop("boolean", "True when the concept holds in every repository, not just this one."),
            ["cwd"] = Prop("string", "The working directory, used to tell which repository the concept belongs to."),
        },
        ["required"] = new JsonArray { "term", "definition" },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var anchors = McpAnchors.Read(arguments, out var rejected);
        var global = McpArgs.GetBool(arguments, "global") ?? false;
        var cwd = McpArgs.GetString(arguments, "cwd") ?? Directory.GetCurrentDirectory();

        var result = await services.GetRequiredService<KnowledgePlacement>().SaveConceptAsync(new ConceptInput
        {
            Term = McpArgs.GetRequiredString(arguments, "term"),
            Definition = McpArgs.GetRequiredString(arguments, "definition"),
            Aliases = McpAnchors.Strings(arguments, "aliases"),
            Anchors = anchors,
            Repository = global ? null : TurnPayload.RepositoryScopeName(cwd),
        }, cancellationToken).ConfigureAwait(false);

        return new JsonObject
        {
            ["saved"] = true,
            ["created"] = result.Created,
            ["id"] = result.Concept.Id,
            ["term"] = result.Concept.Trigger,
            ["scope"] = result.Concept.ScopeValue.Length == 0 ? "global" : result.Concept.ScopeValue,
            ["anchors"] = new JsonArray([.. RuleAnchors.Parse(result.Concept.Anchors).Select(a => (JsonNode)a.ToString())]),
            ["glossary"] = new JsonArray([.. result.GlossaryTerms.Select(t => (JsonNode)new JsonObject
            {
                ["term"] = t.Term,
                ["canonical"] = t.Canonical,
            })]),
            ["rejected_anchors"] = new JsonArray([.. rejected.Select(r => (JsonNode)r)]),
        };
    }

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static JsonObject StringArray(string description) => new()
    {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "string" },
        ["description"] = description,
    };
}

/// <summary>Reads the <c>paths</c> and <c>symbols</c> arguments the placement tools share.</summary>
internal static class McpAnchors
{
    /// <summary>The anchors in <c>paths</c> and <c>symbols</c>; any that do not parse are returned in <paramref name="rejected"/>.</summary>
    public static IReadOnlyList<RuleAnchor> Read(JsonObject? arguments, out IReadOnlyList<string> rejected)
    {
        var anchors = new List<RuleAnchor>();
        var bad = new List<string>();
        var written = Strings(arguments, "paths").Select(p => "path:" + p)
            .Concat(Strings(arguments, "symbols").Select(s => "symbol:" + s));

        foreach (var text in written)
        {
            if (RuleAnchors.TryParse(text, out var anchor))
            {
                anchors.Add(anchor);
            }
            else
            {
                bad.Add(text);
            }
        }

        rejected = bad;
        return anchors;
    }

    public static IReadOnlyList<string> Strings(JsonObject? arguments, string key) =>
        arguments?[key] is JsonArray array
            ? array.OfType<JsonValue>()
                .Select(v => v.TryGetValue<string>(out var text) ? text : null)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!)
                .ToList()
            : [];
}
