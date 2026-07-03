using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Seeds;

/// <summary>
/// Default <see cref="ISeedPackService"/>. Materialises catalog packs into seed-derived
/// <see cref="RecallRule"/> rows and back, deterministically and idempotently.
/// </summary>
public sealed class SeedPackService : ISeedPackService
{
    /// <summary>Initial confidence for a freshly installed seed rule (moderate, earned trust only).</summary>
    public const double InitialConfidence = 0.65;

    private readonly IRecallRuleRepository _rules;

    public SeedPackService(IRecallRuleRepository rules) =>
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));

    public async Task<IReadOnlyList<SeedPackListing>> ListAsync(CancellationToken cancellationToken = default)
    {
        var installedPacks = await InstalledPackNamesAsync(cancellationToken).ConfigureAwait(false);

        return SeedPackCatalog.All
            .Select(pack => new SeedPackListing
            {
                Name = pack.Name,
                Description = pack.Description,
                RuleCount = pack.Rules.Count,
                Installed = installedPacks.Contains(pack.Name),
            })
            .ToList();
    }

    public async Task<SeedPackDetail?> ShowAsync(string packName, CancellationToken cancellationToken = default)
    {
        var pack = SeedPackCatalog.Find(packName);
        if (pack is null)
        {
            return null;
        }

        var installed = await LoadPackRulesAsync(pack.Name, cancellationToken).ConfigureAwait(false);
        var byKey = installed
            .GroupBy(r => r.SeedRuleKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var rules = pack.Rules
            .Select(def =>
            {
                byKey.TryGetValue(def.Key, out var existing);
                return new SeedPackRuleView
                {
                    Key = def.Key,
                    Title = def.Title,
                    RuleId = existing?.Id,
                    Status = existing?.Status,
                };
            })
            .ToList();

        return new SeedPackDetail
        {
            Name = pack.Name,
            Description = pack.Description,
            CopyrightNote = pack.CopyrightNote,
            RuleCount = pack.Rules.Count,
            DefaultStatus = RuleStatus.Active,
            DefaultConfidence = InitialConfidence,
            Installed = installed.Count > 0,
            Rules = rules,
        };
    }

    public async Task<SeedInstallResult> InstallAsync(string packName, SeedInstallOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var pack = SeedPackCatalog.Find(packName)
            ?? throw new KeyNotFoundException($"Unknown seed pack '{packName}'. Run 'agentrecall seed list' to see available packs.");

        // Installing a pack is the opt-in: its rules are Active from day one. --suggested
        // is the conservative mode that keeps them Pending for manual approval.
        var targetStatus = options.Suggested ? RuleStatus.Pending : RuleStatus.Active;
        var existing = await LoadPackRulesAsync(pack.Name, cancellationToken).ConfigureAwait(false);
        var byKey = existing
            .GroupBy(r => r.SeedRuleKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var toAdd = new List<RecallRule>();
        var toUpdate = new List<RecallRule>();
        var changes = new List<SeedRuleChange>();

        foreach (var def in pack.Rules)
        {
            byKey.TryGetValue(def.Key, out var current);

            if (current is null)
            {
                var rule = BuildRule(pack, def, targetStatus);
                toAdd.Add(rule);
                changes.Add(Change(def, null, SeedRuleOutcome.Added));
                continue;
            }

            if (current.Status == RuleStatus.Archived)
            {
                if (!options.Force)
                {
                    changes.Add(Change(def, current.Id, SeedRuleOutcome.SkippedArchived));
                }
                else if (IsUserModified(current, def))
                {
                    changes.Add(Change(def, current.Id, SeedRuleOutcome.SkippedUserModified));
                }
                else
                {
                    // Restore a cleanly removed rule to the target status and initial trust.
                    current.Status = targetStatus;
                    current.Confidence = InitialConfidence;
                    current.Deprecated = false;
                    toUpdate.Add(current);
                    changes.Add(Change(def, current.Id, SeedRuleOutcome.Restored));
                }

                continue;
            }

            // Already present in a live status: leave it exactly as the user has it.
            changes.Add(Change(def, current.Id, SeedRuleOutcome.SkippedExisting));
        }

        if (toAdd.Count > 0)
        {
            await _rules.AddRangeAsync(toAdd, cancellationToken).ConfigureAwait(false);
        }

        if (toUpdate.Count > 0)
        {
            await _rules.UpdateRangeAsync(toUpdate, cancellationToken).ConfigureAwait(false);
        }

        // Backfill ids assigned on insert onto the change list.
        var addedByKey = toAdd.ToDictionary(r => r.SeedRuleKey, StringComparer.Ordinal);
        for (var i = 0; i < changes.Count; i++)
        {
            if (changes[i].Outcome == SeedRuleOutcome.Added && addedByKey.TryGetValue(changes[i].Key, out var added))
            {
                changes[i] = changes[i] with { RuleId = added.Id };
            }
        }

        return new SeedInstallResult
        {
            Pack = pack.Name,
            Status = targetStatus,
            Confidence = InitialConfidence,
            Changes = changes,
            AffectedRules = [.. toAdd, .. toUpdate],
        };
    }

    public async Task<SeedRemoveResult> RemoveAsync(string packName, bool force = false, CancellationToken cancellationToken = default)
    {
        var pack = SeedPackCatalog.Find(packName)
            ?? throw new KeyNotFoundException($"Unknown seed pack '{packName}'. Run 'agentrecall seed list' to see available packs.");

        var installed = await LoadPackRulesAsync(pack.Name, cancellationToken).ConfigureAwait(false);
        var defByKey = pack.Rules.ToDictionary(d => d.Key, StringComparer.Ordinal);

        var toArchive = new List<RecallRule>();
        var changes = new List<SeedRuleChange>();

        foreach (var rule in installed)
        {
            if (rule.Status == RuleStatus.Archived)
            {
                continue; // already removed
            }

            defByKey.TryGetValue(rule.SeedRuleKey, out var def);
            var title = def?.Title ?? rule.Trigger;

            // Preserve rules the user made their own — unless they force removal.
            if (!force && (def is null ? rule.Version > 1 || rule.Status == RuleStatus.Promoted : IsUserModified(rule, def)))
            {
                changes.Add(new SeedRuleChange { Key = rule.SeedRuleKey, Title = title, RuleId = rule.Id, Outcome = SeedRuleOutcome.Preserved });
                continue;
            }

            rule.Status = RuleStatus.Archived;
            toArchive.Add(rule);
            changes.Add(new SeedRuleChange { Key = rule.SeedRuleKey, Title = title, RuleId = rule.Id, Outcome = SeedRuleOutcome.Archived });
        }

        if (toArchive.Count > 0)
        {
            await _rules.UpdateRangeAsync(toArchive, cancellationToken).ConfigureAwait(false);
        }

        return new SeedRemoveResult { Pack = pack.Name, Changes = changes };
    }

    public async Task<IReadOnlyList<SeedPackStatus>> StatusAsync(CancellationToken cancellationToken = default)
    {
        var all = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        var seeds = all.Where(r => r.Source == RuleSource.BuiltInSeed).ToList();

        return SeedPackCatalog.All
            .Select(pack =>
            {
                var packRules = seeds
                    .Where(r => string.Equals(r.SeedPack, pack.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var inForce = packRules.Where(r => r.Status != RuleStatus.Archived).ToList();
                var avg = inForce.Count == 0 ? 0.0 : Math.Round(inForce.Average(r => r.Confidence), 2);

                return new SeedPackStatus
                {
                    Name = pack.Name,
                    Description = pack.Description,
                    TotalRules = pack.Rules.Count,
                    Installed = packRules.Count > 0,
                    Active = packRules.Count(r => r.Status == RuleStatus.Active),
                    Suggested = packRules.Count(r => r.Status is RuleStatus.Pending or RuleStatus.Draft),
                    Promoted = packRules.Count(r => r.Status == RuleStatus.Promoted),
                    Archived = packRules.Count(r => r.Status == RuleStatus.Archived),
                    AverageConfidence = avg,
                };
            })
            .ToList();
    }

    private RecallRule BuildRule(SeedPackDefinition pack, SeedRuleDefinition def, RuleStatus status)
    {
        var because = string.IsNullOrWhiteSpace(def.Exception)
            ? def.Because
            : $"{def.Because} Exception: {def.Exception}";

        return new RecallRule
        {
            Status = status,
            Source = RuleSource.BuiltInSeed,
            SeedPack = pack.Name,
            SeedRuleKey = def.Key,
            Category = def.Category,
            Trigger = def.Trigger,
            RuleText = def.Action,
            Mistake = def.AntiPattern,
            TechnicalContext = because,
            Tags = ComposeTags(pack.Name, def.Tags),
            Confidence = InitialConfidence,
            ScopeLevel = ScopeLevel.Global,
            ScopeValue = string.Empty,
            CaptureReason = CaptureReason.BuiltInSeed,
            EvidenceSummary = $"Installed from the '{pack.Name}' built-in seed pack (starter guidance, not a project observation).",
        };
    }

    /// <summary>The canonical "Because" text for a definition, matching <see cref="BuildRule"/>.</summary>
    private static string CanonicalBecause(SeedRuleDefinition def) =>
        string.IsNullOrWhiteSpace(def.Exception) ? def.Because : $"{def.Because} Exception: {def.Exception}";

    /// <summary>
    /// True when the user has diverged from the shipped seed content: edited the wording,
    /// promoted it, or created a new version. Such a rule is treated as the user's own and
    /// is never overwritten or silently deleted.
    /// </summary>
    private static bool IsUserModified(RecallRule rule, SeedRuleDefinition def) =>
        rule.Status == RuleStatus.Promoted
        || rule.Version > 1
        || !string.Equals(rule.Trigger, def.Trigger, StringComparison.Ordinal)
        || !string.Equals(rule.RuleText, def.Action, StringComparison.Ordinal)
        || !string.Equals(rule.Mistake, def.AntiPattern, StringComparison.Ordinal)
        || !string.Equals(rule.TechnicalContext, CanonicalBecause(def), StringComparison.Ordinal);

    private static SeedRuleChange Change(SeedRuleDefinition def, int? ruleId, SeedRuleOutcome outcome) =>
        new() { Key = def.Key, Title = def.Title, RuleId = ruleId, Outcome = outcome };

    /// <summary>Prefixes the universal seed tags ("seed" and the pack name) onto a rule's own tags.</summary>
    public static string ComposeTags(string packName, string ruleTags)
    {
        var tags = new List<string> { "seed", packName };
        tags.AddRange(ruleTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.Join(", ", tags.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private async Task<List<RecallRule>> LoadPackRulesAsync(string packName, CancellationToken cancellationToken)
    {
        var all = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        return all
            .Where(r => r.Source == RuleSource.BuiltInSeed
                && string.Equals(r.SeedPack, packName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<HashSet<string>> InstalledPackNamesAsync(CancellationToken cancellationToken)
    {
        var all = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        return all
            .Where(r => r.Source == RuleSource.BuiltInSeed && !string.IsNullOrEmpty(r.SeedPack))
            .Select(r => r.SeedPack)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
