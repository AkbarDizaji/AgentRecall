using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Compression;

/// <summary>The synthesized content of a canonical rule merged from several rules.</summary>
public sealed record CanonicalRule
{
    public required string RuleText { get; init; }
    public required string Trigger { get; init; }
    public required string Mistake { get; init; }
    public required string Tags { get; init; }
    public required string TechnicalContext { get; init; }
}

/// <summary>
/// Synthesizes a single canonical rule from a group of related rules. The default
/// implementation is deterministic; an LLM-backed generator can be swapped in.
/// </summary>
public interface ICanonicalRuleGenerator
{
    CanonicalRule Generate(IReadOnlyList<RecallRule> sources);
}
