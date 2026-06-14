using System.Text.Json.Nodes;
using AgentRecall.Core.Compression;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// MCP tool: detect duplicate, near-duplicate and overlapping rules (and repeated
/// corrections) and compress each group into one canonical rule. Defaults to a
/// dry run so the agent can preview the candidates before applying.
/// </summary>
public sealed class CompressMemoryTool : IMcpTool
{
    public string Name => "compress_memory";

    public string Description =>
        "Reduce memory duplication: find duplicate, near-duplicate and overlapping " +
        "rules and merge each group into a single canonical rule. Originals and " +
        "their feedback are preserved as an audit trail. Runs as a dry run by " +
        "default (dry_run=false to apply). Reports rules merged, memory reduction " +
        "percentage and candidate compressions.";

    public JsonObject InputSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["dry_run"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Preview candidates without changing anything (default true).",
            },
            ["scope_level"] = ScopeLevelProp(),
            ["scope_value"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Restrict compression to this scope identifier (e.g. repo name).",
            },
            ["overlap_threshold"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Token-overlap (0–1) at or above which rules are grouped (default 0.34).",
            },
        },
    };

    public async Task<JsonNode> InvokeAsync(JsonObject? arguments, IServiceProvider services, CancellationToken cancellationToken)
    {
        var dryRun = McpArgs.GetBool(arguments, "dry_run") ?? true;

        var options = new CompressionOptions
        {
            ScopeLevel = McpToolHelpers.TryParseScopeLevel(McpArgs.GetString(arguments, "scope_level"), out var level)
                ? level
                : null,
            ScopeValue = McpArgs.GetString(arguments, "scope_value"),
            OverlapThreshold = McpArgs.GetDouble(arguments, "overlap_threshold") is { } t and >= 0 and <= 1
                ? t
                : CompressionOptions.Default.OverlapThreshold,
        };

        var service = services.GetRequiredService<IMemoryCompressionService>();

        if (dryRun)
        {
            var analysis = await service.AnalyzeAsync(options, cancellationToken).ConfigureAwait(false);
            return new JsonObject
            {
                ["dry_run"] = true,
                ["statistics"] = StatsNode(analysis.Stats),
                ["candidates"] = CandidateArray(analysis.Candidates),
            };
        }

        var result = await service.CompressAsync(options, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["dry_run"] = false,
            ["statistics"] = StatsNode(result.Stats),
            ["compressed"] = CompressedArray(result.Groups),
        };
    }

    private static JsonObject StatsNode(CompressionStats stats) => new()
    {
        ["rules_merged"] = stats.RulesMerged,
        ["canonical_rules_created"] = stats.CanonicalRulesCreated,
        ["candidate_compressions"] = stats.CandidateCompressions,
        ["rules_before"] = stats.RulesBefore,
        ["rules_after"] = stats.RulesAfter,
        ["memory_reduction_percentage"] = stats.MemoryReductionPercentage,
    };

    private static JsonArray CandidateArray(IReadOnlyList<CompressionCandidate> candidates)
    {
        var array = new JsonArray();
        foreach (var candidate in candidates)
        {
            array.Add(new JsonObject
            {
                ["canonical_rule"] = candidate.CanonicalRuleText,
                ["relationship"] = candidate.Relationship.ToString(),
                ["subject"] = candidate.Subject,
                ["source_ids"] = new JsonArray([.. candidate.Sources.Select(s => (JsonNode)s.Id)]),
                ["sources"] = new JsonArray([.. candidate.Sources.Select(s => (JsonNode)s.RuleText)]),
            });
        }

        return array;
    }

    private static JsonArray CompressedArray(IReadOnlyList<CompressedGroup> groups)
    {
        var array = new JsonArray();
        foreach (var group in groups)
        {
            array.Add(new JsonObject
            {
                ["canonical_rule_id"] = group.Canonical.Id,
                ["canonical_rule"] = group.Canonical.RuleText,
                ["relationship"] = group.Relationship.ToString(),
                ["subject"] = group.Subject,
                ["superseded_ids"] = new JsonArray([.. group.Sources.Select(s => (JsonNode)s.Id)]),
                ["audit_event_id"] = group.AuditEventId,
            });
        }

        return array;
    }

    private static JsonObject ScopeLevelProp()
    {
        var prop = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Scope granularity to restrict compression to.",
        };

        var enumValues = new JsonArray();
        foreach (var name in Enum.GetNames<ScopeLevel>())
        {
            enumValues.Add(name);
        }

        prop["enum"] = enumValues;
        return prop;
    }
}
