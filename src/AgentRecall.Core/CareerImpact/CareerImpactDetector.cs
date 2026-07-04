using System.Text;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.CareerImpact;

/// <summary>
/// The cheap, deterministic end-of-turn analyzer. It inspects the turn's text (user prompt,
/// assistant response, and any captured rule text) with keyword/heuristic signals and decides
/// whether the turn involved significant, promotion-worthy engineering work — then derives the
/// impact, evidence, metrics, stakeholders, and ADR coaching detail from the same signals.
///
/// It makes no LLM calls, no embeddings, and no network requests, so it is safe to run on the
/// end-of-turn path. It is a pure function of its input and never stays chatty for trivial
/// turns (typo fixes, renames, formatting) — those carry no positive signal.
/// </summary>
public sealed class CareerImpactDetector
{
    /// <summary>One keyword signal: the phrases that fire it, the impact it evidences, and its reason.</summary>
    private sealed record Signal(string[] Phrases, ImpactCategory[] Categories, string Reason, bool Strong);

    // Positive signals. "Strong" signals make a turn significant on their own; two weak signals
    // together also clear the bar. Phrases are matched whole-word (single word) or as a phrase.
    private static readonly Signal[] PositiveSignals =
    [
        new(["migration", "migrate", "migrations", "migrating"], [ImpactCategory.Architecture, ImpactCategory.TechnicalImpact], "involves a migration", true),
        new(["architecture", "architectural", "design decision", "api design"], [ImpactCategory.Architecture], "makes an architectural decision", true),
        new(["incident", "outage", "postmortem", "post-mortem"], [ImpactCategory.IncidentResponse, ImpactCategory.Reliability], "responds to an incident or outage", true),
        new(["optimization", "optimize", "optimized", "optimizing", "optimisation"], [ImpactCategory.Performance], "optimizes a system", true),
        new(["performance", "latency", "throughput"], [ImpactCategory.Performance], "targets performance", true),
        new(["reliability", "availability", "slo", "sla"], [ImpactCategory.Reliability], "improves reliability", true),
        new(["security", "vulnerability", "auth", "authentication", "authorization"], [ImpactCategory.Security], "has a security dimension", true),
        new(["platform"], [ImpactCategory.Architecture, ImpactCategory.CrossTeamImpact], "changes a shared platform", true),
        new(["cross-team", "cross team", "cross-functional"], [ImpactCategory.CrossTeamImpact], "spans multiple teams", true),
        new(["scaling", "scalability", "scale"], [ImpactCategory.Performance, ImpactCategory.Architecture], "affects scaling", true),
        new(["standardization", "standardize", "standardise"], [ImpactCategory.ProcessImprovement, ImpactCategory.Architecture], "standardizes practice", true),
        new(["mentoring", "mentor", "mentored"], [ImpactCategory.Leadership], "involves mentoring", true),
        new(["design review", "technical strategy", "knowledge sharing"], [ImpactCategory.Leadership, ImpactCategory.LongTermLeverage], "shows technical leadership", true),

        new(["refactor", "refactoring", "refactored"], [ImpactCategory.TechnicalImpact], "refactors code with broader impact", false),
        new(["automation", "automate", "automated"], [ImpactCategory.DeveloperProductivity, ImpactCategory.ProcessImprovement], "automates a workflow", false),
        new(["internal tooling", "tooling"], [ImpactCategory.DeveloperProductivity], "builds internal tooling", false),
        new(["developer productivity", "developer experience"], [ImpactCategory.DeveloperProductivity], "improves developer productivity", false),
        new(["rollout", "launch", "launched"], [ImpactCategory.CrossTeamImpact, ImpactCategory.UserImpact], "involves a rollout or launch", false),
        new(["stakeholder", "stakeholders"], [ImpactCategory.CrossTeamImpact], "affects stakeholders", false),
        new(["metrics", "dashboard", "monitoring"], [ImpactCategory.PromotionEvidence, ImpactCategory.Reliability], "adds metrics or monitoring", false),
        new(["cost reduction", "cloud cost", "cost"], [ImpactCategory.Cost], "reduces cost", false),
        new(["process improvement"], [ImpactCategory.ProcessImprovement], "improves a process", false),
        new(["manual workflow", "repeated manual"], [ImpactCategory.DeveloperProductivity, ImpactCategory.ProcessImprovement], "removes a repeated manual workflow", false),
        new(["reviewer accepted", "accepted the design", "accepted the approach", "accepted my design"], [ImpactCategory.Leadership, ImpactCategory.PromotionEvidence], "a reviewer accepted the design or approach", false),
        new(["adr", "architecture decision record", "tradeoff", "trade-off"], [ImpactCategory.Architecture, ImpactCategory.Documentation], "records an architectural decision", false),
        new(["documentation", "design doc", "runbook"], [ImpactCategory.Documentation], "produces durable documentation", false),
    ];

    // Noise markers that mean the turn was trivial. They suppress a weak-only candidate; a
    // strong positive signal (a real migration, incident, …) still wins over them.
    private static readonly string[] NegativeSignals =
    [
        "typo", "simple rename", "just rename", "rename this", "formatting", "format only",
        "one-line", "one line", "trivial", "casual", "small docs", "wording", "whitespace",
        "lint fix", "prompt only", "just a prompt",
    ];

    /// <summary>Analyzes the turn text and returns a deterministic career-impact assessment.</summary>
    public CareerImpactAnalysis Analyze(CareerImpactInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var haystack = BuildHaystack(input);
        var tokens = Tokenize(haystack);

        var matched = PositiveSignals
            .Where(s => s.Phrases.Any(p => Mentions(haystack, tokens, p)))
            .ToList();
        var negative = NegativeSignals.Any(n => Mentions(haystack, tokens, n));

        var strongCount = matched.Count(s => s.Strong);
        var weakCount = matched.Count(s => !s.Strong);
        var hasSignal = matched.Count > 0;

        // Significant when a strong signal fires, or two or more weak signals do. Trivial
        // noise markers suppress a weak-only candidate but never override a strong signal.
        var isSignificant = strongCount >= 1 || weakCount >= 2;
        if (negative && strongCount == 0 && weakCount < 3)
        {
            isSignificant = false;
        }

        var categories = matched
            .SelectMany(s => s.Categories)
            .Distinct()
            .OrderBy(c => (int)c)
            .ToList();

        var reasons = matched.Select(s => s.Reason).Distinct().Take(6).ToList();

        var confidence = Confidence(strongCount, weakCount, isSignificant);
        var worthiness = Math.Clamp((int)Math.Round(confidence * 10), 0, 10);

        var adr = BuildAdr(categories, haystack, tokens);
        var metrics = SuggestMetrics(categories);
        var evidence = SuggestEvidence(categories, adr.Recommended);
        var stakeholders = SuggestStakeholders(categories, haystack, tokens, isSignificant);
        var compactSummary = isSignificant
            ? "possible Staff-level impact detected"
            : "possible engineering impact detected";

        var nextActions = BuildNextActions(evidence, adr.Recommended);

        return new CareerImpactAnalysis
        {
            IsSignificant = isSignificant,
            HasSignal = hasSignal,
            Confidence = Math.Round(confidence, 2),
            PromotionWorthiness = worthiness,
            Categories = categories,
            Reasons = reasons,
            SuggestedMetrics = metrics,
            SuggestedEvidence = evidence,
            Stakeholders = stakeholders,
            NextActions = nextActions,
            Adr = adr,
            WhyThisMatters = BuildWhy(reasons),
            TechnicalImpact = BuildTechnical(categories),
            BusinessImpact = BuildBusiness(categories),
            LongTermImpact = BuildLongTerm(categories),
            PromotionNote = BuildPromotionNote(reasons, isSignificant),
            CompactSummary = compactSummary,
        };
    }

    private static double Confidence(int strongCount, int weakCount, bool isSignificant)
    {
        if (!isSignificant)
        {
            // A lower-confidence candidate surfaced only in Always mode.
            return Math.Clamp(0.3 + 0.05 * (strongCount + weakCount), 0.0, 0.5);
        }

        var score = strongCount >= 1
            ? 0.6 + 0.08 * (strongCount - 1) + 0.05 * weakCount
            : 0.5 + 0.06 * weakCount;
        return Math.Clamp(score, 0.0, 0.95);
    }

    private static CareerImpactAdr BuildAdr(IReadOnlyList<ImpactCategory> categories, string haystack, HashSet<string> tokens)
    {
        var recommended = categories.Contains(ImpactCategory.Architecture)
            || Mentions(haystack, tokens, "adr")
            || Mentions(haystack, tokens, "tradeoff")
            || Mentions(haystack, tokens, "trade-off")
            || Mentions(haystack, tokens, "design decision");

        if (!recommended)
        {
            return new CareerImpactAdr { Recommended = false };
        }

        var title =
            Mentions(haystack, tokens, "migration") || Mentions(haystack, tokens, "migrate") ? "Record the migration decision" :
            Mentions(haystack, tokens, "api design") ? "Record the API design decision" :
            Mentions(haystack, tokens, "platform") ? "Record the platform architecture decision" :
            "Record the architecture decision";

        return new CareerImpactAdr
        {
            Recommended = true,
            SuggestedTitle = title,
            Context = "A significant technical decision was made this turn; capture the situation that motivated it while it is fresh.",
            Decision = "Document the option chosen and the reasoning behind it.",
            Alternatives = ["Alternative approaches considered", "Deferring or doing nothing"],
            Consequences = ["Follow-on work and tradeoffs accepted", "Impact on other teams and systems"],
        };
    }

    private static List<string> SuggestMetrics(IReadOnlyList<ImpactCategory> categories)
    {
        var metrics = new List<string>();
        void Add(string m) { if (!metrics.Contains(m, StringComparer.OrdinalIgnoreCase)) metrics.Add(m); }

        if (categories.Contains(ImpactCategory.Performance))
        {
            Add("latency");
            Add("error rate");
            Add("adoption");
        }

        if (categories.Contains(ImpactCategory.Reliability) || categories.Contains(ImpactCategory.IncidentResponse))
        {
            Add("error rate");
            Add("availability");
            Add("recovery time (MTTR)");
        }

        if (categories.Contains(ImpactCategory.Cost))
        {
            Add("cloud cost");
        }

        if (categories.Contains(ImpactCategory.DeveloperProductivity) || categories.Contains(ImpactCategory.ProcessImprovement))
        {
            Add("developer productivity");
            Add("deployment frequency");
        }

        if (categories.Contains(ImpactCategory.CrossTeamImpact) || categories.Contains(ImpactCategory.UserImpact))
        {
            Add("adoption");
        }

        return metrics.Take(6).ToList();
    }

    private static List<string> SuggestEvidence(IReadOnlyList<ImpactCategory> categories, bool adrRecommended)
    {
        var evidence = new List<string> { "PR", "before/after metrics", "dashboard" };
        if (adrRecommended)
        {
            evidence.Add("ADR");
        }

        if (categories.Contains(ImpactCategory.IncidentResponse))
        {
            evidence.Add("incident timeline");
        }

        if (categories.Contains(ImpactCategory.Architecture))
        {
            evidence.Add("design doc");
        }

        return evidence.Take(6).ToList();
    }

    private static List<string> SuggestStakeholders(
        IReadOnlyList<ImpactCategory> categories,
        string haystack,
        HashSet<string> tokens,
        bool isSignificant)
    {
        var stakeholders = new List<string>();
        void Add(string s) { if (!stakeholders.Contains(s, StringComparer.OrdinalIgnoreCase)) stakeholders.Add(s); }

        if (categories.Contains(ImpactCategory.Architecture)) Add("Platform");
        if (categories.Contains(ImpactCategory.CrossTeamImpact)) Add("Engineering");
        if (categories.Contains(ImpactCategory.UserImpact)) Add("Product");
        if (categories.Contains(ImpactCategory.Security)) Add("Security");
        if (categories.Contains(ImpactCategory.Reliability) || categories.Contains(ImpactCategory.IncidentResponse)) Add("SRE");
        if (categories.Contains(ImpactCategory.DeveloperProductivity)) Add("Developer Experience");
        if (categories.Contains(ImpactCategory.Cost)) Add("Management");

        if (Mentions(haystack, tokens, "product")) Add("Product");
        if (Mentions(haystack, tokens, "support")) Add("Support");
        if (Mentions(haystack, tokens, "management")) Add("Management");

        if (stakeholders.Count == 0 && isSignificant)
        {
            Add("Platform");
            Add("Product");
        }

        return stakeholders.Take(5).ToList();
    }

    private static List<string> BuildNextActions(IReadOnlyList<string> evidence, bool adrRecommended)
    {
        var actions = new List<string>
        {
            $"Collect evidence: {string.Join(", ", evidence)}",
        };
        if (adrRecommended)
        {
            actions.Add("Write an ADR capturing the decision, alternatives, and consequences");
        }

        actions.Add("Run `agentrecall career journal --last` for a promotion-ready entry");
        return actions;
    }

    private static string BuildWhy(IReadOnlyList<string> reasons)
    {
        if (reasons.Count == 0)
        {
            return "This turn may carry engineering impact worth capturing.";
        }

        var top = reasons.Take(2).ToList();
        return "This work " + string.Join("; it ", top) + ".";
    }

    private static string BuildTechnical(IReadOnlyList<ImpactCategory> categories)
    {
        var areas = new List<string>();
        if (categories.Contains(ImpactCategory.Architecture)) areas.Add("system architecture");
        if (categories.Contains(ImpactCategory.Performance)) areas.Add("performance");
        if (categories.Contains(ImpactCategory.Reliability) || categories.Contains(ImpactCategory.IncidentResponse)) areas.Add("reliability");
        if (categories.Contains(ImpactCategory.Security)) areas.Add("security");
        return areas.Count > 0
            ? $"Changes {string.Join(", ", areas)} characteristics."
            : "Improves the codebase through reusable engineering work.";
    }

    private static string BuildBusiness(IReadOnlyList<ImpactCategory> categories)
    {
        var areas = new List<string>();
        if (categories.Contains(ImpactCategory.Cost)) areas.Add("cost");
        if (categories.Contains(ImpactCategory.UserImpact)) areas.Add("users");
        if (categories.Contains(ImpactCategory.CrossTeamImpact)) areas.Add("multiple teams");
        return areas.Count > 0
            ? $"Affects {string.Join(", ", areas)}."
            : "Indirect business impact via engineering quality and velocity.";
    }

    private static string BuildLongTerm(IReadOnlyList<ImpactCategory> categories)
    {
        var leverage = categories.Contains(ImpactCategory.LongTermLeverage)
            || categories.Contains(ImpactCategory.Architecture)
            || categories.Contains(ImpactCategory.ProcessImprovement)
            || categories.Contains(ImpactCategory.DeveloperProductivity);
        return leverage
            ? "Creates reusable leverage beyond the immediate task."
            : "Mostly near-term impact; consider how to make it reusable.";
    }

    private static string BuildPromotionNote(IReadOnlyList<string> reasons, bool isSignificant)
    {
        var lead = isSignificant ? "Staff-level engineering work" : "Engineering work";
        if (reasons.Count == 0)
        {
            return $"{lead} worth capturing for a future review.";
        }

        return $"{lead} that {JoinWithAnd(reasons.Take(2))}.";
    }

    /// <summary>Joins phrases into natural prose: "a", "a and b", "a, b, and c".</summary>
    private static string JoinWithAnd(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        return list.Count switch
        {
            0 => string.Empty,
            1 => list[0],
            2 => $"{list[0]} and {list[1]}",
            _ => $"{string.Join(", ", list.Take(list.Count - 1))}, and {list[^1]}",
        };
    }

    private static string BuildHaystack(CareerImpactInput input)
    {
        var sb = new StringBuilder();
        sb.Append(input.Prompt ?? string.Empty).Append('\n');
        sb.Append(input.Response ?? string.Empty).Append('\n');
        foreach (var rule in input.CapturedRuleTexts)
        {
            sb.Append(rule).Append('\n');
        }

        return sb.ToString().ToLowerInvariant();
    }

    private static HashSet<string> Tokenize(string haystack)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var current = new StringBuilder();
        foreach (var ch in haystack)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    // A single-word phrase matches whole-word (so "scale" does not fire on "escalate"); a
    // multi-word phrase or one containing punctuation is matched as a substring.
    private static bool Mentions(string haystack, HashSet<string> tokens, string phrase) =>
        phrase.All(char.IsLetterOrDigit)
            ? tokens.Contains(phrase)
            : haystack.Contains(phrase, StringComparison.Ordinal);
}
