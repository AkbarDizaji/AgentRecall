using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Compression;

/// <summary>
/// Default <see cref="IMemoryCompressionService"/>. Groups compressible rules with
/// <see cref="RuleSimilarity"/>, synthesizes a canonical rule per group with an
/// <see cref="ICanonicalRuleGenerator"/>, and supersedes the originals so they
/// (and their feedback events) survive as an audit trail.
/// </summary>
public sealed class MemoryCompressionService : IMemoryCompressionService
{
    /// <summary>Confidence added per extra corroborating rule, capped at 1.0.</summary>
    private const double CorroborationBonus = 0.1;

    private readonly IRecallRuleRepository _rules;
    private readonly IRecallEventRepository _events;
    private readonly ICanonicalRuleGenerator _generator;

    public MemoryCompressionService(
        IRecallRuleRepository rules,
        IRecallEventRepository events,
        ICanonicalRuleGenerator generator)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
    }

    public async Task<CompressionAnalysis> AnalyzeAsync(CompressionOptions options, CancellationToken cancellationToken = default)
    {
        options ??= CompressionOptions.Default;

        var compressible = await CompressibleRulesAsync(options, cancellationToken).ConfigureAwait(false);
        var clusters = RuleSimilarity.Cluster(compressible, options);

        var candidates = clusters
            .Select(c => new CompressionCandidate
            {
                CanonicalRuleText = _generator.Generate(c.Rules).RuleText,
                Sources = c.Rules,
                Relationship = c.Relationship,
                Subject = c.Subject,
            })
            .ToList();

        var stats = BuildStats(
            compressible.Count,
            clusters.Count,
            clusters.Sum(c => c.Rules.Count));

        return new CompressionAnalysis { Candidates = candidates, Stats = stats };
    }

    public async Task<CompressionResult> CompressAsync(CompressionOptions options, CancellationToken cancellationToken = default)
    {
        options ??= CompressionOptions.Default;

        var compressible = await CompressibleRulesAsync(options, cancellationToken).ConfigureAwait(false);
        var clusters = RuleSimilarity.Cluster(compressible, options);

        var groups = new List<CompressedGroup>();
        var mergedCount = 0;

        foreach (var cluster in clusters)
        {
            var now = DateTimeOffset.UtcNow;
            var canonicalContent = _generator.Generate(cluster.Rules);

            var canonical = await _rules.AddAsync(new RecallRule
            {
                Version = 1,
                Status = RuleStatus.Active,
                Trigger = canonicalContent.Trigger,
                Mistake = canonicalContent.Mistake,
                RuleText = canonicalContent.RuleText,
                TechnicalContext = canonicalContent.TechnicalContext,
                Tags = canonicalContent.Tags,
                Confidence = CanonicalConfidence(cluster.Rules),
                ScopeLevel = cluster.Rules[0].ScopeLevel,
                ScopeValue = cluster.Rules[0].ScopeValue,
                SupersedesRuleId = cluster.Rules.OrderBy(r => r.Id).First().Id,
                CreatedAt = now,
            }, cancellationToken).ConfigureAwait(false);

            // Preserve each original: mark it superseded by — but never delete —
            // the canonical rule. Its feedback events stay untouched.
            foreach (var source in cluster.Rules)
            {
                source.Status = RuleStatus.Superseded;
                source.SupersededById = canonical.Id;
                source.UpdatedAt = now;
                await _rules.UpdateAsync(source, cancellationToken).ConfigureAwait(false);
            }

            mergedCount += cluster.Rules.Count;

            var sourceIds = string.Join(", ", cluster.Rules.Select(r => $"#{r.Id}"));
            var auditEvent = await _events.AddAsync(new RecallEvent
            {
                Type = RecallEventType.RulesCompressed,
                RuleId = canonical.Id,
                Trigger = "compress_memory",
                Details =
                    $"Merged {cluster.Rules.Count} {cluster.Relationship} rule(s) [{sourceIds}] on \"{cluster.Subject}\" " +
                    $"into canonical rule #{canonical.Id}: \"{canonical.RuleText}\". " +
                    "Source rules and their feedback are preserved.",
            }, cancellationToken).ConfigureAwait(false);

            groups.Add(new CompressedGroup
            {
                Canonical = canonical,
                Sources = cluster.Rules,
                Relationship = cluster.Relationship,
                Subject = cluster.Subject,
                AuditEventId = auditEvent.Id,
            });
        }

        var stats = BuildStats(compressible.Count, groups.Count, mergedCount);
        return new CompressionResult { Groups = groups, Stats = stats };
    }

    private async Task<List<RecallRule>> CompressibleRulesAsync(CompressionOptions options, CancellationToken cancellationToken)
    {
        var all = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);

        return all
            .Where(r => r.Status is RuleStatus.Active or RuleStatus.Promoted && !r.Deprecated)
            .Where(r => options.ScopeLevel is null || r.ScopeLevel == options.ScopeLevel)
            .Where(r => string.IsNullOrWhiteSpace(options.ScopeValue)
                || string.Equals(r.ScopeValue, options.ScopeValue, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static double CanonicalConfidence(IReadOnlyList<RecallRule> sources)
    {
        var max = sources.Max(s => s.Confidence);
        return Math.Round(Math.Min(1.0, max + CorroborationBonus * (sources.Count - 1)), 2);
    }

    private static CompressionStats BuildStats(int rulesBefore, int groups, int rulesMerged)
    {
        // Each group of N originals collapses to 1 canonical: N-1 rules removed.
        var removed = rulesMerged - groups;
        var rulesAfter = rulesBefore - removed;
        var reduction = rulesBefore == 0 ? 0 : (double)removed / rulesBefore * 100;

        return new CompressionStats
        {
            CandidateCompressions = groups,
            RulesMerged = rulesMerged,
            CanonicalRulesCreated = groups,
            RulesBefore = rulesBefore,
            RulesAfter = rulesAfter,
            MemoryReductionPercentage = Math.Round(reduction, 2),
        };
    }
}
