using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRecall.Core.Domain;

namespace AgentRecall.Cli.Mcp;

/// <summary>
/// Agent-facing projection of a <see cref="RecallRule"/>. Field names serialize
/// to snake_case (do_not, applies_to) to match the MCP tool contract.
/// </summary>
public sealed record RuleGuidance
{
    public required int Id { get; init; }
    public required string Trigger { get; init; }
    public required string Rule { get; init; }
    public required string Do { get; init; }
    public required string DoNot { get; init; }
    public required string Reason { get; init; }
    public required string AppliesTo { get; init; }
    public required double Confidence { get; init; }
    public required string Status { get; init; }

    public static RuleGuidance From(RecallRule rule) => new()
    {
        Id = rule.Id,
        Trigger = rule.Trigger,
        Rule = rule.RuleText,
        Do = rule.RuleText,
        DoNot = rule.Mistake,
        Reason = BuildReason(rule),
        AppliesTo = BuildAppliesTo(rule),
        Confidence = Math.Round(rule.Confidence, 2),
        Status = rule.Status.ToString(),
    };

    private static string BuildReason(RecallRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.TechnicalContext))
        {
            return rule.TechnicalContext;
        }

        return string.IsNullOrWhiteSpace(rule.Mistake)
            ? "Derived from prior feedback."
            : $"Avoids the mistake: {rule.Mistake}";
    }

    private static string BuildAppliesTo(RecallRule rule) =>
        rule.ScopeLevel == ScopeLevel.Global || string.IsNullOrWhiteSpace(rule.ScopeValue)
            ? rule.ScopeLevel.ToString()
            : $"{rule.ScopeLevel}:{rule.ScopeValue}";
}

/// <summary>Shared JSON options for MCP payloads (snake_case, web defaults).</summary>
public static class McpJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };
}
