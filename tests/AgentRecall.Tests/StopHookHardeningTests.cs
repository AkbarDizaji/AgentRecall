using AgentRecall.Cli;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests for the deterministic Stop-hook quality gate (<see cref="StopHookCandidateGate"/>) and
/// the <c>cleanup pending-noise</c> command. The gate no longer decides live capture — the
/// semantic capture judge does (see <see cref="CaptureJudgeFinalizerTests"/>) — but it is still
/// the shared screen behind <c>cleanup pending-noise</c>, which finds and archives noisy rules
/// created before the judge existed. Everything here is offline and deterministic.
/// </summary>
[Collection("ConsoleStdin")]
public class StopHookHardeningTests
{
    private static async Task Init(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
    }

    private static async Task<IReadOnlyList<RecallRule>> Rules(TestDatabase db)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().ListAsync();
    }

    // ---- Gate unit checks (deterministic, no DB) ------------------------------

    [Fact]
    public void Gate_OneThingWorthSavingProse_IsAssistantProse()
    {
        var result = StopHookCandidateGate.ScreenText(
            "One thing is worth saving — a workflow gotcha not in any doc.");
        Assert.False(result.IsAcceptable);
        Assert.Equal(CaptureSkipReason.AssistantProse, result.Reason);
    }

    [Fact]
    public void Gate_WantMeToSaveIt_IsAssistantProse()
    {
        Assert.Equal(CaptureSkipReason.AssistantProse,
            StopHookCandidateGate.ScreenText("Want me to save it?").Reason);
    }

    [Fact]
    public void Gate_IDidntManuallyCall_IsAssistantProse()
    {
        Assert.Equal(CaptureSkipReason.AssistantProse,
            StopHookCandidateGate.ScreenText("I didn't manually call AgentRecall, the hook fires on its own.").Reason);
    }

    [Fact]
    public void Gate_StopHookMayHaveCaptured_IsAssistantProse()
    {
        Assert.Equal(CaptureSkipReason.AssistantProse,
            StopHookCandidateGate.ScreenText("The Stop hook may have captured it.").Reason);
    }

    [Fact]
    public void Gate_MalformedConversationTrigger_IsRejected()
    {
        Assert.True(StopHookCandidateGate.IsMalformedTrigger(
            "When working on Not much. Most of this chat lives here."));

        var assessed = StopHookCandidateGate.Assess(
            "Keep JSON on stdout and status on stderr.",
            "When working on Not much. Most of this chat lives here.");
        Assert.Equal(CaptureSkipReason.MalformedTrigger, assessed.Reason);
    }

    [Fact]
    public void Gate_ConditionWithNoAction_IsMissingAction()
    {
        Assert.Equal(CaptureSkipReason.MissingAction,
            StopHookCandidateGate.ScreenText("When reporting the state to the user.").Reason);
    }

    [Fact]
    public void Gate_MissingTrigger_IsMalformed()
    {
        Assert.Equal(CaptureSkipReason.MalformedTrigger,
            StopHookCandidateGate.Assess("Keep resources tidy and dispose them.", triggerText: null).Reason);
    }

    [Fact]
    public void Gate_ConditionActionNoReason_IsAccepted()
    {
        Assert.True(StopHookCandidateGate.ScreenText("When writing SQL, use parameterized queries.").IsAcceptable);
    }

    [Fact]
    public void Gate_CleanAgentRecallConvention_IsAccepted()
    {
        Assert.True(StopHookCandidateGate.ScreenText(
            "When reporting AgentRecall memory state, check capture-status or turn-summary and answer from actual state instead of guessing.").IsAcceptable);
    }

    // An off-topic aside is rejected even when it contains a keyword ("validation") that would
    // otherwise read as a security concern — the "Off topic:" opener marks it a digression.
    [Fact]
    public void Gate_OffTopicAsideWithKeyword_IsOffTopic()
    {
        Assert.Equal(CaptureSkipReason.OffTopic,
            StopHookCandidateGate.ScreenText(
                "Off topic: in the registration modal we change the button according to validation " +
                "and say the event is full and you can join the waitlist.").Reason);
    }

    [Theory]
    [InlineData("Unrelated, but the dashboard loads slowly on Safari.")]
    [InlineData("Side note: the marketing site uses a different colour palette.")]
    [InlineData("By the way, the staging URL changed last week.")]
    public void Gate_TangentOpeners_AreOffTopic(string text)
    {
        Assert.Equal(CaptureSkipReason.OffTopic, StopHookCandidateGate.ScreenText(text).Reason);
    }

    [Fact]
    public void Gate_TangentWordMidRule_IsAccepted()
    {
        Assert.True(StopHookCandidateGate.ScreenText(
            "When validating input, run every check on the same code path to avoid going off on a tangent.")
            .IsAcceptable);
    }

    // Every literal in DoNotSaveSignals must independently trigger rejection, and a clean
    // candidate containing none of them must not. Together with the existing acceptance
    // tests above, this pins down each signal literal (L87-91): if any one were replaced
    // with an empty string, ContainsAny would start matching unconditionally and the
    // clean-candidate assertions elsewhere in this file would fail.
    [Fact]
    public void Gate_EveryDoNotSaveSignal_TriggersExplicitDoNotSave()
    {
        foreach (var phrase in StopHookCandidateGate.DoNotSaveSignals)
        {
            var result = StopHookCandidateGate.ScreenText($"Please {phrase} for this task, it is not needed.");
            Assert.Equal(CaptureSkipReason.ExplicitDoNotSave, result.Reason);
        }
    }

    // ---- ScreenText: null/whitespace guard (kills the L159-161 block-removal mutant,
    // which would let a null candidate fall through to candidateText.Trim() and throw) --------

    [Fact]
    public void Gate_NullCandidate_IsTooVague()
    {
        Assert.Equal(CaptureSkipReason.TooVague, StopHookCandidateGate.ScreenText(null).Reason);
    }

    [Fact]
    public void Gate_WhitespaceCandidate_IsTooVague()
    {
        Assert.Equal(CaptureSkipReason.TooVague, StopHookCandidateGate.ScreenText("   ").Reason);
    }

    // A vague signal ("fyi") paired with a real action verb and enough words that the
    // short-candidate fallback would otherwise accept it. This distinguishes the IsVague
    // rejection (L182-184) from the fallback path, and separately proves IsVague's own
    // "return true" (L297) actually drives the result rather than being dead code.
    [Fact]
    public void Gate_VagueSignalWithActionVerb_IsStillTooVague()
    {
        Assert.Equal(CaptureSkipReason.TooVague, StopHookCandidateGate.ScreenText(
            "FYI, always validate permissions before deleting records to avoid accidental data loss.").Reason);
    }

    // Exactly ShortCandidateWords (8) words, no action verb, no condition opener: the
    // boundary where "<=" (accept path skipped, reject fires) diverges from "<" (accept
    // path taken) at L190, and where the ternary at L193 must actually evaluate
    // HasCondition (false here) rather than being forced to the "true" branch.
    [Fact]
    public void Gate_ExactlyShortCandidateWordBoundary_NoVerbNoCondition_IsTooVague()
    {
        Assert.Equal(CaptureSkipReason.TooVague,
            StopHookCandidateGate.ScreenText("The quick brown fox jumps over lazy dogs").Reason);
    }

    // 4 words: skips IsVague's "< 4" fragment rule, reaches the ShortCandidateWords
    // fallback with HasCondition true -> MissingAction (kills the L302 "<=4" equality
    // mutant, which would instead let IsVague intercept and force TooVague).
    [Fact]
    public void Gate_ConditionOpenerNoVerb_FourWords_IsMissingAction()
    {
        Assert.Equal(CaptureSkipReason.MissingAction,
            StopHookCandidateGate.ScreenText("When four random words").Reason);
    }

    // Short (3 words, so IsVague's "< 4" clause is in play) but carries a real action verb.
    // Kills the L302 LogicalNot un-negate mutant (ContainsAny without the "!"), which would
    // make IsVague reject any short phrase that DOES contain an action verb instead of one
    // that lacks one.
    [Fact]
    public void Gate_ShortPhraseWithActionVerb_IsAccepted()
    {
        Assert.True(StopHookCandidateGate.ScreenText("Always check permissions").IsAcceptable);
    }

    // Fewer than 4 words, no action verb, no condition opener: IsVague's own "< 4" branch
    // (not the VagueSignals branch) must return true (L304) and pre-empt the HasCondition
    // check entirely -- if it instead returned false (mutant), the fallback would notice the
    // "when " opener and report MissingAction instead of TooVague.
    [Fact]
    public void Gate_ShortNoVerbButHasConditionOpener_StillTooVague()
    {
        Assert.Equal(CaptureSkipReason.TooVague,
            StopHookCandidateGate.ScreenText("When something breaks").Reason);
    }

    // Neither a vague signal, an action verb, nor a condition opener, at a length that
    // clears IsVague's word-count rule (4-8 words): every HasCondition clause (L311-317)
    // must genuinely evaluate to false here. If any single StartsWith/Contains literal in
    // HasCondition were replaced with "" (always matches), this would flip to MissingAction.
    [Fact]
    public void Gate_NoVerbNoConditionMidLength_IsTooVague()
    {
        Assert.Equal(CaptureSkipReason.TooVague,
            StopHookCandidateGate.ScreenText("Something odd happened yesterday afternoon").Reason);
    }

    [Theory]
    [InlineData("When four random things happen")]
    [InlineData("If four random things happen")]
    [InlineData("While four random things happen")]
    // Note: no "Whenever ..." case here -- "whenever" itself contains "never " as a
    // substring, which is one of the ActionVerbs signals, so any "whenever"-opened
    // sentence always reads as having an action verb regardless of the rest of the text.
    [InlineData("Before four random things happen")]
    [InlineData("After four random things happen")]
    [InlineData("Notice when four random things happen")]
    public void Gate_ConditionOpenersOrMidClause_AreMissingActionNotTooVague(string text)
    {
        Assert.Equal(CaptureSkipReason.MissingAction, StopHookCandidateGate.ScreenText(text).Reason);
    }

    // ---- Assess: body-rejection short-circuit and trigger ternary --------------

    // The rejected body's reason must win outright, even though the trigger itself is
    // perfectly well-formed. Kills the L207-209 block-removal mutant, which would fall
    // through and evaluate the trigger instead of returning the rejected body.
    [Fact]
    public void Assess_RejectedBody_WellFormedTrigger_KeepsBodyReason()
    {
        Assert.Equal(CaptureSkipReason.ExplicitDoNotSave, StopHookCandidateGate.Assess(
            "Do not save this note please.", "When deploying to production.").Reason);
    }

    // An acceptable body with a well-formed (non-malformed) trigger must be accepted overall.
    // Kills the L211-213 conditional-true mutant, which would force MalformedTrigger
    // regardless of the actual (false) IsMalformedTrigger result.
    [Fact]
    public void Assess_AcceptableBody_WellFormedTrigger_IsAccepted()
    {
        var result = StopHookCandidateGate.Assess(
            "Use parameterized queries for all SQL to avoid injection risk.", "When writing SQL queries.");
        Assert.True(result.IsAcceptable);
        Assert.Equal(CaptureSkipReason.None, result.Reason);
    }

    // ---- IsMalformedTrigger: opener-stripping and its downstream checks --------

    // Each of these strips down to a single-word subject via a distinct opener literal
    // ("when working on ", "when ", "if ", "while ", "whenever "). If that specific opener's
    // string were replaced with "" (Stryker's L229 mutation), StartsWith("") would match
    // trivially at that position, the real prefix would never be stripped, and the
    // resulting (longer, unstripped) subject would no longer trip the < 2 word-count rule.
    // The "When deploying" case also kills the L234 statement-removal mutant (which drops
    // the subject re-assignment) and the L251 equality mutant ("> 2" instead of "< 2").
    [Theory]
    [InlineData("When working on databases")]
    [InlineData("When deploying")]
    [InlineData("If broken")]
    [InlineData("While testing")]
    [InlineData("Whenever deployed")]
    public void IsMalformedTrigger_OpenerStrippedToSingleWord_IsMalformed(string trigger)
    {
        Assert.True(StopHookCandidateGate.IsMalformedTrigger(trigger));
    }

    // The complementary boundary for the L251 equality mutant: three words after stripping
    // is a real (non-malformed) condition, not just "not >= 2".
    [Fact]
    public void IsMalformedTrigger_OpenerStrippedToThreeWords_IsNotMalformed()
    {
        Assert.False(StopHookCandidateGate.IsMalformedTrigger("When shipping new features"));
    }

    // A trigger with no fragment and no short subject, but a mid-string sentence break, is
    // still a conversation fragment. Kills the L246 boolean mutant (return true -> return
    // false) for the sentence-break rule, which currently has zero coverage.
    [Fact]
    public void IsMalformedTrigger_MidStringSentenceBreak_IsMalformed()
    {
        Assert.True(StopHookCandidateGate.IsMalformedTrigger("When shipping code. Ship daily."));
    }

    // ---- ContainsDoNotSave (currently zero coverage) ---------------------------

    [Fact]
    public void ContainsDoNotSave_NullText_IsFalse()
    {
        // Also guards against the L256 mutations that drop or invert the null/whitespace
        // guard: those would evaluate text.ToLowerInvariant()/text.Trim() on a null
        // reference and throw instead of returning false.
        Assert.False(StopHookCandidateGate.ContainsDoNotSave(null));
    }

    [Fact]
    public void ContainsDoNotSave_WhitespaceText_IsFalse()
    {
        Assert.False(StopHookCandidateGate.ContainsDoNotSave("   "));
    }

    [Fact]
    public void ContainsDoNotSave_EmptyText_IsFalse()
    {
        Assert.False(StopHookCandidateGate.ContainsDoNotSave(string.Empty));
    }

    [Fact]
    public void ContainsDoNotSave_MatchingPhrase_IsTrue()
    {
        // Kills the LogicalNotExpression un-negate mutant at L256: without the "!", a
        // non-blank, matching string would incorrectly evaluate to false.
        Assert.True(StopHookCandidateGate.ContainsDoNotSave("Please do not save this."));
    }

    [Fact]
    public void ContainsDoNotSave_CleanTextNoSignal_IsFalse()
    {
        Assert.False(StopHookCandidateGate.ContainsDoNotSave("Keep this rule around, it is useful."));
    }

    // ---- Explain: every reason has its own distinct, non-empty message --------
    // Each case of the L259 switch has zero coverage; Stryker's "" string mutation on any
    // one arm is only caught by asserting the exact expected text for that specific reason.

    [Theory]
    [InlineData(CaptureSkipReason.None, "not stored")]
    [InlineData(CaptureSkipReason.ExplicitDoNotSave, "explicit do-not-save instruction")]
    [InlineData(CaptureSkipReason.AssistantProse, "assistant prose, not a reusable rule")]
    [InlineData(CaptureSkipReason.MalformedTrigger, "malformed trigger, not a reusable rule")]
    [InlineData(CaptureSkipReason.MissingAction, "no actionable guidance")]
    [InlineData(CaptureSkipReason.MissingReason, "missing reason or consequence")]
    [InlineData(CaptureSkipReason.TooVague, "too vague to be a reusable rule")]
    [InlineData(CaptureSkipReason.DuplicateNoise, "duplicate noisy candidate")]
    [InlineData(CaptureSkipReason.CodeFact, "code fact, recoverable from the repository")]
    [InlineData(CaptureSkipReason.NotReusable, "not a reusable lesson")]
    [InlineData(CaptureSkipReason.SourceDocument, "source-document instruction, not a reusable rule")]
    [InlineData(CaptureSkipReason.ToolOrSkillInstruction, "tool or skill instruction, not a reusable rule")]
    [InlineData(CaptureSkipReason.CommandOutput, "command output, not a reusable rule")]
    [InlineData(CaptureSkipReason.LogOutput, "log output, not a reusable rule")]
    [InlineData(CaptureSkipReason.OffTopic, "off-topic aside, not a reusable rule")]
    public void Gate_Explain_ReturnsExpectedMessage(CaptureSkipReason reason, string expected)
    {
        Assert.Equal(expected, StopHookCandidateGate.Explain(reason));
    }

    [Fact]
    public void Gate_Explain_UnknownReasonValue_FallsBackToNotStored()
    {
        Assert.Equal("not stored", StopHookCandidateGate.Explain((CaptureSkipReason)999));
    }

    // ---- IsOffTopicAside: "off topic" and "off-topic" are independent triggers -----------
    // Neither variant implies the other, so an "||" is required. Each test below contains
    // exactly one of the two substrings, which would flip from OffTopic to some other
    // reason under an "&&" mutation (L284-285).

    [Fact]
    public void Gate_OffTopicHyphenOnly_IsOffTopic()
    {
        Assert.Equal(CaptureSkipReason.OffTopic, StopHookCandidateGate.ScreenText(
            "Honestly this feels off-topic for our current sprint but let's keep going anyway.").Reason);
    }

    [Fact]
    public void Gate_OffTopicSpaceOnly_IsOffTopic()
    {
        Assert.Equal(CaptureSkipReason.OffTopic, StopHookCandidateGate.ScreenText(
            "Quick note, this feels off topic given our current focus, but noted anyway.").Reason);
    }

    // ---- cleanup pending-noise ------------------------------------------------

    private static async Task<RecallRule> AddRuleAsync(
        TestDatabase db, string ruleText, string trigger, RuleStatus status, string tags = "turn-finalizer", int version = 1)
    {
        await using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRecallRuleRepository>().AddAsync(new RecallRule
        {
            RuleText = ruleText,
            Trigger = trigger,
            Status = status,
            Tags = tags,
            Version = version,
            Category = RuleCategory.RepositoryConvention,
            ScopeLevel = ScopeLevel.Repository,
            ScopeValue = "project",
            Confidence = 0.4,
        });
    }

    private static async Task SeedNoiseAsync(TestDatabase db)
    {
        await AddRuleAsync(db, "One thing is worth saving — a workflow gotcha not in any doc.", "When working on Not much", RuleStatus.Pending);
        await AddRuleAsync(db, "Want me to save it?", "When working on this chat", RuleStatus.Pending);
        await AddRuleAsync(db, "Keep JSON on stdout and status on stderr.", "When working on Not much. Most of this chat lives here", RuleStatus.Pending);
        // A clean Pending rule that must be preserved.
        await AddRuleAsync(db, "When writing SQL, use parameterized queries because injection is a risk.", "When writing SQL", RuleStatus.Pending);
        // An Active rule (never touched by cleanup).
        await AddRuleAsync(db, "Blah blah not much really here.", "When working on chat", RuleStatus.Active);
        // A user-modified (versioned) Pending rule (never touched).
        await AddRuleAsync(db, "One thing is worth saving here.", "When working on chat", RuleStatus.Pending, version: 2);
    }

    private static async Task<(int Code, string Output)> RunAsync(TestDatabase db, params string[] args)
    {
        var writer = new StringWriter();
        var code = await CommandRouter.RunAsync(args, db.Services, writer);
        return (code, writer.ToString());
    }

    // T. Cleanup dry-run finds noisy Pending turn-finalizer rules and archives nothing.
    [Fact]
    public async Task T_CleanupDryRun_FindsNoiseArchivesNothing()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedNoiseAsync(db);

        var (code, output) = await RunAsync(db, "cleanup", "pending-noise");

        Assert.Equal(0, code);
        Assert.Contains("found", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--apply", output, StringComparison.Ordinal);
        Assert.DoesNotContain(await Rules(db), r => r.Status == RuleStatus.Archived);
    }

    // U. Cleanup --apply archives noisy Pending turn-finalizer rules.
    [Fact]
    public async Task U_CleanupApply_ArchivesNoise()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedNoiseAsync(db);

        var (code, output) = await RunAsync(db, "cleanup", "pending-noise", "--apply");

        Assert.Equal(0, code);
        Assert.Contains("archived", output, StringComparison.OrdinalIgnoreCase);
        var archived = (await Rules(db)).Where(r => r.Status == RuleStatus.Archived).ToList();
        Assert.NotEmpty(archived);
        Assert.All(archived, r => Assert.Contains("turn-finalizer", r.Tags, StringComparison.Ordinal));
    }

    // V/W/X. Cleanup preserves Active, clean Pending, and user-modified rules.
    [Fact]
    public async Task VWX_Cleanup_PreservesActiveCleanAndUserModified()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedNoiseAsync(db);

        await RunAsync(db, "cleanup", "pending-noise", "--apply");

        var rules = await Rules(db);
        Assert.Contains(rules, r => r.Status == RuleStatus.Active);
        Assert.Contains(rules, r =>
            r.Status == RuleStatus.Pending && r.RuleText.Contains("parameterized queries", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rules, r => r.Version > 1 && r.Status != RuleStatus.Archived);
    }

    // Y. Cleanup JSON is valid and deterministic.
    [Fact]
    public async Task Y_CleanupJson_IsValidAndShaped()
    {
        await using var db = new TestDatabase();
        await Init(db);
        await SeedNoiseAsync(db);

        var (code, output) = await RunAsync(db, "cleanup", "pending-noise", "--json");

        Assert.Equal(0, code);
        var node = System.Text.Json.Nodes.JsonNode.Parse(output)!;
        Assert.True(node["matched"]!.GetValue<int>() >= 1);
        Assert.Equal(0, node["archived"]!.GetValue<int>());
        Assert.True(node["dryRun"]!.GetValue<bool>());
        Assert.NotNull(node["reasons"]);
    }
}
