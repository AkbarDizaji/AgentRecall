using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Text;

namespace AgentRecall.Core.Concepts;

/// <summary>What to save as a domain concept.</summary>
public sealed record ConceptInput
{
    /// <summary>The term, in English — the word rules and tasks use for it ("event").</summary>
    public required string Term { get; init; }

    /// <summary>What the term means here.</summary>
    public required string Definition { get; init; }

    /// <summary>
    /// Other names for it. One in another language ("ایونت") is also added to the glossary,
    /// so a prompt in that language reaches the concept and every rule about the term.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>The code it lives in, so it is delivered when the work reaches that code.</summary>
    public IReadOnlyList<RuleAnchor> Anchors { get; init; } = [];

    /// <summary>The repository it belongs to; null or empty for a concept that holds everywhere.</summary>
    public string? Repository { get; init; }
}

/// <summary>The concept as stored, and what saving it changed.</summary>
public sealed record ConceptSaveResult(RecallRule Concept, bool Created, IReadOnlyList<GlossaryTerm> GlossaryTerms);

/// <summary>
/// Puts knowledge in its place: saves the business and technical concepts that code cannot
/// explain, and pins existing rules to the paths and symbols they belong to. Both the CLI and the
/// MCP server go through here so a concept or an anchor saved either way is the same row.
///
/// A concept is stored as a <see cref="RecallRule"/> of category
/// <see cref="RuleCategory.DomainConcept"/> — the term as its trigger, the definition as its text,
/// the term and its other names as tags — so it is ranked, deduplicated, archived, and reported on
/// by the same machinery as every rule, and only rendered differently.
/// </summary>
public sealed class KnowledgePlacement
{
    private const int MaxTermLength = 100;
    private const int MaxDefinitionLength = 1000;

    private readonly IRecallRuleRepository _rules;
    private readonly IGlossaryTermRepository _glossary;

    public KnowledgePlacement(IRecallRuleRepository rules, IGlossaryTermRepository glossary)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _glossary = glossary ?? throw new ArgumentNullException(nameof(glossary));
    }

    /// <summary>
    /// Saves a concept, or updates the one already saved under the same term in the same
    /// repository — re-saving refines a definition instead of leaving two that disagree.
    /// </summary>
    public async Task<ConceptSaveResult> SaveConceptAsync(ConceptInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var term = input.Term.Trim();
        var definition = input.Definition.Trim();
        if (term.Length == 0 || term.Length > MaxTermLength)
        {
            throw new ArgumentException($"A concept needs a term of 1–{MaxTermLength} characters.", nameof(input));
        }

        if (definition.Length == 0 || definition.Length > MaxDefinitionLength)
        {
            throw new ArgumentException($"A concept needs a definition of 1–{MaxDefinitionLength} characters.", nameof(input));
        }

        var repository = string.IsNullOrWhiteSpace(input.Repository) ? string.Empty : input.Repository.Trim();
        var aliases = input.Aliases
            .Select(a => a.Trim())
            .Where(a => a.Length > 0 && !string.Equals(Glossary.Normalize(a), Glossary.Normalize(term), StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var tags = string.Join(",", aliases.Prepend(term).Select(t => t.Replace(",", " ", StringComparison.Ordinal)));

        var match = (await _rules.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(r => r.Category == RuleCategory.DomainConcept && RuleStatusSets.IsEffective(r))
            .FirstOrDefault(r =>
                string.Equals(Glossary.Normalize(r.Trigger), Glossary.Normalize(term), StringComparison.Ordinal)
                && string.Equals(r.ScopeValue, repository, StringComparison.OrdinalIgnoreCase));

        // The listing is untracked; update through the tracked instance, or a concept saved earlier
        // in the same scope collides with its own copy.
        var existing = match is null ? null : await _rules.GetAsync(match.Id, cancellationToken).ConfigureAwait(false);

        RecallRule concept;
        if (existing is null)
        {
            concept = await _rules.AddAsync(new RecallRule
            {
                Status = RuleStatus.Active,
                Category = RuleCategory.DomainConcept,
                Trigger = term,
                RuleText = definition,
                Tags = tags,
                Anchors = RuleAnchors.Format(input.Anchors),
                Confidence = 0.9,
                CaptureReason = Capture.CaptureReason.ManualFeedback,
                ScopeLevel = repository.Length == 0 ? ScopeLevel.Global : ScopeLevel.Repository,
                ScopeValue = repository,
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            existing.RuleText = definition;
            existing.Tags = string.Join(",",
                existing.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Concat(tags.Split(','))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            existing.Anchors = RuleAnchors.Format(RuleAnchors.Parse(existing.Anchors).Concat(input.Anchors));
            concept = await _rules.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        // A name in another script is how the user will say it; the glossary maps it to the term so
        // the prompt matches the concept and every English rule about it. An English alias stays a
        // tag only — mapping it would rewrite English tasks and blur the rules that use that word.
        var glossaryTerms = new List<GlossaryTerm>();
        foreach (var alias in aliases.Where(IsOtherScript))
        {
            glossaryTerms.Add(await MapAsync(alias, term, cancellationToken).ConfigureAwait(false));
        }

        return new ConceptSaveResult(concept, existing is null, glossaryTerms);
    }

    /// <summary>
    /// Adds and removes anchors on a rule and returns it, or null when there is no such rule.
    /// Adding an anchor the rule already has, or removing one it lacks, changes nothing.
    /// </summary>
    public async Task<RecallRule?> PlaceRuleAsync(
        int ruleId,
        IReadOnlyCollection<RuleAnchor> add,
        IReadOnlyCollection<RuleAnchor> remove,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);

        var rule = await _rules.GetAsync(ruleId, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            return null;
        }

        var anchors = RuleAnchors.Parse(rule.Anchors).Concat(add).Where(a => !remove.Contains(a));
        rule.Anchors = RuleAnchors.Format(anchors);
        return await _rules.UpdateAsync(rule, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Points a glossary term at <paramref name="canonical"/>, adding it when new.</summary>
    private async Task<GlossaryTerm> MapAsync(string term, string canonical, CancellationToken cancellationToken)
    {
        var normalized = Glossary.Normalize(term);
        var english = Glossary.Normalize(canonical);
        var existing = await _glossary.FindByTermAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return await _glossary.AddAsync(new GlossaryTerm { Term = normalized, Canonical = english }, cancellationToken)
                .ConfigureAwait(false);
        }

        existing.Canonical = english;
        return await _glossary.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsOtherScript(string text) => text.Any(c => char.IsLetter(c) && c > 'ɏ');
}
