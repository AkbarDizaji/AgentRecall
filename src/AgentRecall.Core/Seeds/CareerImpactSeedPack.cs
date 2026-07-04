using AgentRecall.Core.Domain;

namespace AgentRecall.Core.Seeds;

/// <summary>
/// The built-in <c>career-impact</c> seed pack: small, conditional rules that help the user
/// notice Staff-level engineering impact, collect evidence, attach metrics and stakeholders,
/// detect ADR-worthy decisions, and turn work into promotion-ready achievements. The guidance
/// is original, paraphrased coaching practice — not a project fact, and never treated as one.
///
/// These rules are ordinary retrieval rules (capped and ranked below learned rules). The
/// pack additionally enables the low-token, deterministic end-of-turn career-impact detector
/// (see <c>AgentRecall.Core.CareerImpact</c>) when it is installed and enabled.
/// </summary>
public static class CareerImpactSeedPack
{
    public const string Name = "career-impact";

    public static SeedPackDefinition Definition { get; } = new()
    {
        Name = Name,
        Description = "Coaching rules for noticing Staff-level impact, evidence, metrics, ADRs, and promotion-ready achievements.",
        CopyrightNote =
            "Original, paraphrased career-impact coaching guidance written as conditional rules. " +
            "No book, article, or third-party text is copied or quoted.",
        Rules =
        [
            new SeedRuleDefinition
            {
                Key = "staff-impact-detector",
                Title = "Detect Staff-level engineering impact",
                Trigger = "The user discusses significant work: a design, migration, optimization, incident, platform change, cross-team implementation, or technical strategy.",
                Action = "Evaluate whether it has Staff-level impact and summarize why it matters, cross-team reach, business/user impact, technical impact, long-term leverage, and promotion worthiness.",
                AntiPattern = "Applying this to trivial local changes or casual discussion.",
                Because = "Significant engineering work is often forgotten unless its impact is captured close to when it happens.",
                Tags = "career-impact, staff, impact, promotion",
            },
            new SeedRuleDefinition
            {
                Key = "evidence-collector",
                Title = "Collect evidence before engineering impact is forgotten",
                Trigger = "Significant engineering work is completed or meaningfully advanced.",
                Action = "Suggest concrete evidence to collect now: metrics, dashboards, before/after comparisons, ADRs, design docs, PRs, announcements, incident timelines, rollout notes.",
                AntiPattern = "Producing a long evidence checklist for trivial work.",
                Because = "Promotion and performance-review narratives are stronger when backed by contemporaneous evidence.",
                Tags = "career-impact, evidence, promotion",
            },
            new SeedRuleDefinition
            {
                Key = "achievement-extractor",
                Title = "Convert work into outcome-oriented achievements",
                Trigger = "A meaningful engineering problem has just been solved.",
                Action = "Rewrite the accomplishment as an outcome: describe the result, impact, scope, adoption, and measurable improvement when available, not just the task done.",
                AntiPattern = "Describing only tasks completed (\"Added Redis cache\") instead of the outcome (\"Reduced API latency via a shared caching layer adopted across three services\").",
                Because = "Outcome-oriented achievements are what performance reviews and promotion packets are built from.",
                Tags = "career-impact, achievement, promotion",
            },
            new SeedRuleDefinition
            {
                Key = "adr-detector",
                Title = "Detect when an ADR is needed",
                Trigger = "An architectural decision is made or a meaningful tradeoff is chosen.",
                Action = "Determine whether an ADR should exist and, if so, suggest a title, context, decision, alternatives, and consequences.",
                AntiPattern = "Recommending an ADR for every small implementation detail.",
                Because = "Architectural decisions need durable context so future engineers understand why the choice was made.",
                Tags = "career-impact, adr, architecture, documentation",
            },
            new SeedRuleDefinition
            {
                Key = "metrics-reminder",
                Title = "Attach measurable success metrics to technical solutions",
                Trigger = "Proposing or reviewing a technical solution with observable impact.",
                Action = "Identify measurable success metrics such as latency, availability, deployment frequency, cloud cost, error rate, recovery time, adoption, or developer productivity.",
                AntiPattern = "Forcing metrics onto tiny changes where measurement would be artificial.",
                Because = "Without metrics, impact is hard to defend or improve.",
                Tags = "career-impact, metrics, impact",
            },
            new SeedRuleDefinition
            {
                Key = "promotion-opportunity",
                Title = "Notice promotion-worthy engineering work",
                Trigger = "Significant engineering work is wrapping up.",
                Action = "Evaluate whether anything discussed is worth remembering for a future promotion or review, and if so summarize it in one concise promotion-ready sentence.",
                AntiPattern = "Generating promotion notes for trivial or routine work.",
                Because = "Small timely notes make promotion packets and performance reviews much easier later.",
                Tags = "career-impact, promotion, journal",
            },
            new SeedRuleDefinition
            {
                Key = "leadership-detector",
                Title = "Recognize Staff-level leadership behaviors",
                Trigger = "The user discusses mentoring, design reviews, cross-team alignment, conflict resolution, documentation, technical strategy, or knowledge sharing.",
                Action = "Highlight the leadership behavior explicitly as impact in its own right.",
                AntiPattern = "Treating Staff impact as only architecture or code volume.",
                Because = "Staff-level impact often comes from alignment, leverage, and judgment rather than only implementation.",
                Tags = "career-impact, leadership, staff",
            },
            new SeedRuleDefinition
            {
                Key = "missing-opportunity-detector",
                Title = "Suggest one way to broaden organizational impact",
                Trigger = "The current task could plausibly have broader organizational impact.",
                Action = "Suggest one small way to expand it: a reusable library, an ADR, a standardized process, an automated workflow, shared learnings, or internal tooling.",
                AntiPattern = "Expanding scope when the user is keeping a task intentionally small or urgent.",
                Because = "Staff-level work often turns local fixes into reusable leverage.",
                Tags = "career-impact, leverage, staff",
            },
            new SeedRuleDefinition
            {
                Key = "stakeholder-detector",
                Title = "Identify stakeholders affected by engineering work",
                Trigger = "Work may affect other groups.",
                Action = "Identify the likely stakeholders — such as Engineering, Platform, Security, SRE, Product, Support, or Management — focusing on the groups actually affected.",
                AntiPattern = "Listing every possible stakeholder instead of the likely affected groups.",
                Because = "Stakeholder awareness improves rollout, communication, and adoption.",
                Tags = "career-impact, stakeholder, communication",
            },
            new SeedRuleDefinition
            {
                Key = "career-journal-entry",
                Title = "Create career journal entries for significant work",
                Trigger = "Significant engineering work was completed or meaningfully advanced.",
                Action = "Generate a concise career journal entry with date, work, impact, evidence, metrics, promotion category, ADR status, and next action.",
                AntiPattern = "Generating journal entries automatically for every conversation.",
                Because = "Small timely notes make promotion packets and performance reviews much easier later.",
                Tags = "career-impact, journal, promotion",
            },
        ],
    };
}
