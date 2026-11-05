namespace AgentRecall.Core.Domain;

/// <summary>
/// One glossary entry: a word or phrase the user says (often in another language, e.g.
/// "ایونت") and the English term rules are written in ("event"). Rules are stored in a
/// single language; the glossary is what lets a prompt in another language still reach
/// them, since recall matches words and has no model to translate with.
/// </summary>
public sealed class GlossaryTerm
{
    public int Id { get; set; }

    /// <summary>The word or phrase as the user says it, stored in its normalized form.</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>The English term it stands for, which recall matches rules on.</summary>
    public string Canonical { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A word the user said for something a rule talks about, and the English term the rule uses
/// for it ("ایونت" → "event", "reservation" → "booking"). Supplied by the judge with a captured
/// rule, so the user's own vocabulary is kept with the rule it was said about.
/// </summary>
public sealed record TermMapping(string Term, string Canonical);
