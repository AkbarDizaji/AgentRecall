using AgentRecall.Cli.Hooks;
using AgentRecall.Core.Concepts;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli;

// The `concept` command group, and `rules anchor`: putting knowledge where it belongs.
public static partial class CommandRouter
{
    /// <summary>
    /// Saves and lists domain concepts — what a business or technical term means here. A concept
    /// belongs to the current repository unless <c>--global</c> says it holds everywhere.
    /// </summary>
    private static async Task<int> ConceptAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var sub = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "list";
        var options = ParseOptions(args);

        if (sub is not ("add" or "list") || (sub == "add" && (args.Length < 3 || args[1].StartsWith("--", StringComparison.Ordinal) || args[2].StartsWith("--", StringComparison.Ordinal))))
        {
            output.WriteLine("Usage:");
            output.WriteLine("  agentrecall concept add \"<term>\" \"<definition>\"");
            output.WriteLine("      [--aliases \"<name>,<name>\"] [--paths \"<pattern>,<pattern>\"] [--symbols \"<name>,<name>\"] [--global]");
            output.WriteLine("  agentrecall concept list [--json]");
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);

        if (sub == "list")
        {
            var concepts = (await scope.ServiceProvider.GetRequiredService<Core.Abstractions.IRecallRuleRepository>()
                    .ListAsync(cancellationToken).ConfigureAwait(false))
                .Where(r => r.Category == RuleCategory.DomainConcept && RuleStatusSets.IsEffective(r))
                .OrderBy(r => r.Trigger, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (options.ContainsKey("json"))
            {
                WriteJson(output, concepts.Select(c => new
                {
                    id = c.Id,
                    term = c.Trigger,
                    definition = c.RuleText,
                    names = c.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    anchors = RuleAnchors.Parse(c.Anchors).Select(a => a.ToString()).ToArray(),
                    scope = c.ScopeValue,
                }).ToList());
                return 0;
            }

            if (concepts.Count == 0)
            {
                output.WriteLine("No concepts yet. Add one with: agentrecall concept add \"<term>\" \"<definition>\"");
                return 0;
            }

            foreach (var concept in concepts)
            {
                WriteConcept(output, concept);
            }

            return 0;
        }

        if (!TryReadAnchors(options, output, out var anchors))
        {
            return 1;
        }

        var input = new ConceptInput
        {
            Term = args[1],
            Definition = args[2],
            Aliases = SplitList(options.GetValueOrDefault("aliases")),
            Anchors = anchors,
            Repository = options.ContainsKey("global") ? null : TurnPayload.RepositoryScopeName(Directory.GetCurrentDirectory()),
        };

        ConceptSaveResult result;
        try
        {
            result = await scope.ServiceProvider.GetRequiredService<KnowledgePlacement>()
                .SaveConceptAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            output.WriteLine(ex.Message);
            return 1;
        }

        output.WriteLine(result.Created ? "Saved concept:" : "Updated concept:");
        WriteConcept(output, result.Concept);
        foreach (var term in result.GlossaryTerms)
        {
            // Each side on its own line: right-to-left and left-to-right text on one line render out of order.
            output.WriteLine("Glossary:");
            output.WriteLine(term.Term);
            output.WriteLine($"  -> {term.Canonical}");
        }

        return 0;
    }

    /// <summary>
    /// <c>rules anchor &lt;id&gt; add|remove &lt;anchor&gt;…</c>: pins a rule to the paths and symbols it
    /// belongs to, or unpins it. An anchor is written <c>path:tests/**</c> or <c>symbol:OrderService</c>.
    /// </summary>
    private static async Task<int> AnchorRuleAsync(
        string[] args,
        IServiceProvider scopedServices,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (args.Length < 3 || !int.TryParse(args[0], out var id) || args[1] is not ("add" or "remove"))
        {
            output.WriteLine("Usage:");
            output.WriteLine("  agentrecall rules anchor <id> add <anchor> [<anchor> ...]");
            output.WriteLine("  agentrecall rules anchor <id> remove <anchor> [<anchor> ...]");
            output.WriteLine("An anchor is path:<pattern> (e.g. path:tests/**) or symbol:<name> (e.g. symbol:OrderService).");
            return 1;
        }

        var anchors = new List<RuleAnchor>();
        foreach (var text in args[2..])
        {
            if (!RuleAnchors.TryParse(text, out var anchor))
            {
                output.WriteLine($"Not an anchor: {text}");
                output.WriteLine("Write path:<pattern> (e.g. path:tests/**) or symbol:<name> (e.g. symbol:OrderService).");
                return 1;
            }

            anchors.Add(anchor);
        }

        var placement = scopedServices.GetRequiredService<KnowledgePlacement>();
        var rule = args[1] == "add"
            ? await placement.PlaceRuleAsync(id, anchors, [], cancellationToken).ConfigureAwait(false)
            : await placement.PlaceRuleAsync(id, [], anchors, cancellationToken).ConfigureAwait(false);
        if (rule is null)
        {
            output.WriteLine($"Rule #{id} not found.");
            return 1;
        }

        var placed = RuleAnchors.Parse(rule.Anchors);
        output.WriteLine(placed.Count == 0
            ? $"Rule #{id} is not anchored to any place."
            : $"Rule #{id} is anchored to: {string.Join(", ", placed)}");
        return 0;
    }

    private static void WriteConcept(TextWriter output, RecallRule concept)
    {
        output.WriteLine($"#{concept.Id} {concept.Trigger}");
        output.WriteLine($"  {concept.RuleText}");

        var names = concept.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => !string.Equals(n, concept.Trigger, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (names.Count > 0)
        {
            output.WriteLine("  Also called:");
            foreach (var name in names)
            {
                output.WriteLine($"    {name}");
            }
        }

        var anchors = RuleAnchors.Parse(concept.Anchors);
        if (anchors.Count > 0)
        {
            output.WriteLine($"  Anchored to: {string.Join(", ", anchors)}");
        }

        output.WriteLine($"  Scope: {(concept.ScopeValue.Length == 0 ? "global" : concept.ScopeValue)}");
    }

    /// <summary>Reads <c>--paths</c> and <c>--symbols</c> into anchors, reporting the first that does not parse.</summary>
    private static bool TryReadAnchors(Dictionary<string, string> options, TextWriter output, out IReadOnlyList<RuleAnchor> anchors)
    {
        var parsed = new List<RuleAnchor>();
        anchors = parsed;
        var written = SplitList(options.GetValueOrDefault("paths")).Select(p => "path:" + p)
            .Concat(SplitList(options.GetValueOrDefault("symbols")).Select(s => "symbol:" + s));

        foreach (var text in written)
        {
            if (!RuleAnchors.TryParse(text, out var anchor))
            {
                output.WriteLine($"Not an anchor: {text}");
                return false;
            }

            parsed.Add(anchor);
        }

        return true;
    }

    private static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "true"
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
