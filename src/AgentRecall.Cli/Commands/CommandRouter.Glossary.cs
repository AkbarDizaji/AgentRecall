using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Text;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli;

// The `glossary` command group: maps the words the user says onto the English terms rules use.
public static partial class CommandRouter
{
    /// <summary>
    /// Adds, lists, and removes glossary entries. Adding a term that is already there points it
    /// at the new English term rather than failing, so a mistaken mapping is fixed by re-adding.
    /// </summary>
    private static async Task<int> GlossaryAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var sub = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "list";
        var json = ParseOptions(args).ContainsKey("json");

        var usageError = sub switch
        {
            "add" when args.Length < 3 || Glossary.Normalize(args[1]).Length == 0 || Glossary.Normalize(args[2]).Length == 0 => true,
            "remove" when args.Length < 2 || Glossary.Normalize(args[1]).Length == 0 => true,
            "add" or "remove" or "list" => false,
            _ => true,
        };
        if (usageError)
        {
            output.WriteLine("Usage:");
            output.WriteLine("  agentrecall glossary add \"<term>\" \"<english term>\"");
            output.WriteLine("  agentrecall glossary remove \"<term>\"");
            output.WriteLine("  agentrecall glossary list [--json]");
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        await EnsureInitializedAsync(scope, cancellationToken).ConfigureAwait(false);
        var glossary = scope.ServiceProvider.GetRequiredService<IGlossaryTermRepository>();

        if (sub == "list")
        {
            var terms = (await glossary.ListAsync(cancellationToken).ConfigureAwait(false))
                .OrderBy(t => t.Canonical, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Term, StringComparer.Ordinal)
                .ToList();
            if (json)
            {
                WriteJson(output, terms.Select(t => new { id = t.Id, term = t.Term, canonical = t.Canonical }).ToList());
                return 0;
            }

            if (terms.Count == 0)
            {
                output.WriteLine("The glossary is empty. Add a term with: agentrecall glossary add \"<term>\" \"<english term>\"");
                return 0;
            }

            foreach (var term in terms)
            {
                output.WriteLine(term.Term);
                output.WriteLine($"  -> {term.Canonical}");
            }

            return 0;
        }

        var normalized = Glossary.Normalize(args[1]);
        var existing = await glossary.FindByTermAsync(normalized, cancellationToken).ConfigureAwait(false);

        if (sub == "remove")
        {
            if (existing is null)
            {
                output.WriteLine($"No glossary entry for: {normalized}");
                return 1;
            }

            await glossary.DeleteAsync(existing.Id, cancellationToken).ConfigureAwait(false);
            output.WriteLine($"Removed from the glossary: {normalized}");
            return 0;
        }

        var canonical = Glossary.Normalize(args[2]);
        if (existing is null)
        {
            await glossary.AddAsync(new GlossaryTerm { Term = normalized, Canonical = canonical }, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            existing.Canonical = canonical;
            await glossary.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
        }

        // Each side on its own line: a right-to-left term and a left-to-right one on the same
        // line render out of order in most terminals.
        output.WriteLine(existing is null ? "Added to the glossary:" : "Updated in the glossary:");
        output.WriteLine(normalized);
        output.WriteLine($"  -> {canonical}");
        return 0;
    }
}
