using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Context;

/// <summary>
/// Recognizes an absolute rule: a standing rule with nothing to bound it, so it is delivered on
/// every task in every repository. That is rarely what a lesson means. A rule about a language's
/// syntax belongs to that language's files and a rule about tests belongs to the tests — both are
/// bounded by anchors and are not absolute. A preference about how to talk to the user is bounded
/// by its subject — every reply — and is not absolute either.
/// </summary>
public static class AbsoluteRule
{
    /// <summary>True when <paramref name="rule"/> applies always, everywhere, with no place to hold it.</summary>
    public static bool Is(RecallRule rule) =>
        rule.AlwaysApply
        && rule.Category is not (RuleCategory.UserPreference or RuleCategory.CommunicationPreference or RuleCategory.DomainConcept)
        && RuleAnchors.Parse(rule.Anchors).Count == 0;

    /// <summary>The warning shown when an absolute rule is saved or a rule is promoted to one.</summary>
    public static string Warning(int ruleId) =>
        $"⚠ Rule #{ruleId} is absolute: it applies to every task in every repository, with nothing to bound it, " +
        "which rarely fits a lesson. If it belongs somewhere, anchor it there — " +
        $"`agentrecall rules anchor {ruleId} add path:**/*.ts` for a syntax rule, `path:tests/**` for a test rule — " +
        $"or `agentrecall rules archive {ruleId}` if it should not hold everywhere.";
}
