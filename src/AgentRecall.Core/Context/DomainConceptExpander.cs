namespace AgentRecall.Core.Context;

/// <summary>
/// Deterministic, LLM-free <see cref="IConceptExpander"/> backed by a curated
/// lexicon of domain groups. Each group's first term names it. A seed token
/// activates every group it appears in; all members of an activated group then
/// relate back to that seed. This is what lets "Add refund support" surface a
/// rule about Money — both live in the money group.
///
/// Swap in an embedding-backed expander to broaden coverage without changing
/// callers.
/// </summary>
public sealed class DomainConceptExpander : IConceptExpander
{
    private static readonly string[][] Groups =
    [
        ["money", "currency", "amount", "price", "cost", "payment", "pay", "refund",
         "charge", "invoice", "billing", "balance", "fee", "discount", "tax", "total",
         "monetary", "wallet", "ledger", "transaction"],
        ["date", "time", "timestamp", "timezone", "datetime", "duration", "calendar", "schedule"],
        ["sql", "query", "queries", "database", "db", "parameterized", "injection", "orm", "migration"],
        ["auth", "authentication", "authorization", "login", "logout", "token", "password",
         "credential", "credentials", "session", "jwt", "oauth", "permission"],
        ["concurrency", "thread", "threads", "lock", "locking", "async", "await", "race", "parallel", "mutex"],
        ["validation", "validate", "sanitize", "input", "schema", "constraint", "invariant"],
        ["logging", "log", "logger", "telemetry", "trace", "metric", "metrics"],
        ["http", "rest", "api", "endpoint", "request", "response", "route", "controller"],
        ["cache", "caching", "memoize", "invalidation", "ttl", "eviction"],
        ["error", "exception", "failure", "retry", "resilience", "timeout", "fault"],
        ["security", "secret", "encryption", "encrypt", "hash", "vulnerability", "xss", "csrf", "sanitization"],
        ["file", "filesystem", "path", "stream", "io", "upload", "download", "blob"],
        ["serialization", "serialize", "deserialize", "json", "xml", "mapping", "dto"],
    ];

    // term -> the groups it belongs to (by group name).
    private static readonly Dictionary<string, List<string>> Index = BuildIndex();

    public ConceptContext Build(IEnumerable<string> seedTokens)
    {
        // A term can belong to more than one group, so each newly-activated group's members
        // are appended to their existing group list rather than overwriting it — otherwise a
        // term shared by two activated groups would only ever relate back to whichever one
        // activated last.
        var termGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var activatedBy = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in seedTokens)
        {
            if (!Index.TryGetValue(seed, out var groups))
            {
                continue;
            }

            foreach (var groupName in groups)
            {
                if (!activatedBy.TryGetValue(groupName, out var seeds))
                {
                    seeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    activatedBy[groupName] = seeds;

                    // Append every member of this newly-activated group to its term list.
                    foreach (var member in MembersOf(groupName))
                    {
                        if (!termGroups.TryGetValue(member, out var memberGroups))
                        {
                            memberGroups = [];
                            termGroups[member] = memberGroups;
                        }

                        memberGroups.Add(groupName);
                    }
                }

                seeds.Add(seed);
            }
        }

        var termGroupsReadOnly = termGroups.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyCollection<string>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
        var activated = activatedBy.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyCollection<string>)kv.Value,
            StringComparer.OrdinalIgnoreCase);

        return new ConceptContext(termGroupsReadOnly, activated);
    }

    private static IEnumerable<string> MembersOf(string groupName) =>
        Groups.First(g => g[0] == groupName);

    private static Dictionary<string, List<string>> BuildIndex()
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups)
        {
            var name = group[0];
            foreach (var term in group)
            {
                if (!index.TryGetValue(term, out var list))
                {
                    list = [];
                    index[term] = list;
                }

                list.Add(name);
            }
        }

        return index;
    }
}
