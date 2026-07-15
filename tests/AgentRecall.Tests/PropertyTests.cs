using System.Text.Json.Nodes;
using AgentRecall.Cli.Hooks;
using AgentRecall.Core.Finalization;
using AgentRecall.Core.Memory;
using AgentRecall.Core.Preferences;
using AgentRecall.Core.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Property-based checks for the deterministic, total building blocks. Rather than pin
/// individual examples, these assert invariants that must hold for every input FsCheck
/// throws at them: the tolerant parsers never throw, the classifiers are total and keep
/// confidence in range, and the keyword extractor never emits blank tokens. FsCheck
/// shrinks any counterexample to a minimal failing input.
/// </summary>
public class PropertyTests
{
    private static readonly MemoryWorthinessClassifier Classifier = new();

    // A single initialized database, shared across property iterations so each generated input
    // does not pay for a fresh schema build. Leaked for the test run (temp dir); acceptable in a
    // test process.
    private static readonly IServiceProvider HookServices = CreateInitializedServices();

    private static IServiceProvider CreateInitializedServices()
    {
        var db = new TestDatabase();
        using var scope = db.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AgentRecall.Core.Abstractions.IDatabaseInitializer>()
            .InitializeAsync().GetAwaiter().GetResult();
        return db.Services;
    }

    // ---- TurnPayload.Parse — the documented "never throws" contract -----------

    [Property]
    public void TurnPayload_Parse_NeverThrows_ForArbitraryText(string? payload)
    {
        // The docstring promises tolerance: "returns null rather than throwing, so the
        // hook never blocks Claude Code." That must hold for any string at all.
        TurnPayload.Parse(payload, TextWriter.Null);
    }

    // ---- PreToolUseHook.RunAsync — the "never blocks a write" contract --------

    [Property]
    public void PreToolUseHook_RunAsync_NeverThrows_ForArbitraryText(string? payload)
    {
        // The hook must never throw regardless of the payload, or it would block a file write.
        // FsCheck throws arbitrary strings — including null, non-JSON, and non-object JSON — and
        // shrinks any counterexample to a minimal failing input.
        PreToolUseHook.RunAsync(payload, HookServices, TextWriter.Null).GetAwaiter().GetResult();
    }

    [Property]
    public Property TurnPayload_Parse_IsTolerant_OfArbitraryJsonObjects()
        => Prop.ForAll(
            ArbitraryPayloadGen().ToArbitrary(),
            json => { TurnPayload.Parse(json, TextWriter.Null); return true; });

    [Property]
    public Property TurnPayload_Parse_IsTolerant_OfArbitraryTranscripts()
        => Prop.ForAll(
            TranscriptPayloadGen().ToArbitrary(),
            json => { TurnPayload.Parse(json, TextWriter.Null); return true; });

    // ---- PullRequestCommentParser.Parse ---------------------------------------

    [Property]
    public void CommentParser_Parse_NeverThrows_ForArbitraryText(string? content)
    {
        PullRequestCommentParser.Parse(content);
    }

    [Property]
    public void CommentParser_ReturnedBodies_AreAlwaysNonEmpty(string? content)
    {
        // Whatever we feed it, no comment is ever produced with a blank body.
        foreach (var comment in PullRequestCommentParser.Parse(content))
        {
            Assert.False(string.IsNullOrWhiteSpace(comment.Body));
        }
    }

    [Property]
    public Property CommentParser_IsTolerant_OfArbitraryJson()
        => Prop.ForAll(
            CommentJsonGen().ToArbitrary(),
            json =>
            {
                foreach (var comment in PullRequestCommentParser.Parse(json))
                {
                    Assert.False(string.IsNullOrWhiteSpace(comment.Body));
                }
            });

    // ---- MemoryWorthinessClassifier.Classify — total, confidence in range -----

    [Property]
    public void Classify_IsTotal_AndKeepsConfidenceInRange(string? candidate)
    {
        var result = Classifier.Classify(candidate!);
        Assert.InRange(result.Confidence, 0.0, 1.0);
    }

    // ---- UserPreferenceRecognizer.Match ---------------------------------------

    [Property]
    public void Match_IsTotal_ForArbitraryText(string? text)
    {
        var match = UserPreferenceRecognizer.Match(text);

        // An unsafe preference is refused, so it never carries a stored rule to persist.
        if (match.IsUnsafe)
        {
            Assert.True(string.IsNullOrEmpty(match.NormalizedRule));
        }
    }

    // ---- KeywordExtractor.Extract ---------------------------------------------

    [Property]
    public void Extract_NeverEmitsBlankOrUppercaseTokens(string? text)
    {
        foreach (var keyword in KeywordExtractor.Extract(text!))
        {
            Assert.False(string.IsNullOrWhiteSpace(keyword));
            Assert.Equal(keyword.ToLowerInvariant(), keyword);
        }
    }

    // ---- StopHookCandidateGate — totality + safety invariants -----------------

    [Property]
    public void Gate_ScreenText_NeverThrows_ForArbitraryText(string? text)
    {
        // The gate runs inside the non-blocking Stop hook, so it must return a verdict for
        // any input rather than throw.
        StopHookCandidateGate.ScreenText(text);
    }

    [Property]
    public void Gate_Assess_NeverThrows_ForArbitraryText(string? candidate, string? trigger)
    {
        StopHookCandidateGate.Assess(candidate, trigger);
    }

    [Property]
    public void Gate_IsMalformedTrigger_NeverThrows_ForArbitraryText(string? trigger)
    {
        StopHookCandidateGate.IsMalformedTrigger(trigger);
    }

    [Property]
    public Property Gate_AnyDoNotSavePhrase_IsAlwaysRejectedAsExplicitDoNotSave()
        // Whatever junk surrounds it, text containing a do-not-save instruction must be
        // rejected as ExplicitDoNotSave — the feature's core safety promise. The gate checks
        // do-not-save before anything else, so the reason is deterministic.
        => Prop.ForAll(
            DoNotSaveWrappedGen().ToArbitrary(),
            t => StopHookCandidateGate
                .ScreenText($"{t.Before} {t.Phrase} {t.After}").Reason == CaptureSkipReason.ExplicitDoNotSave);

    [Property]
    public Property Extractor_TieBreak_PrefersDoNotSave_UnlessSaveIsStrictlyLater()
        => Prop.ForAll(
            Gen.Elements(StopHookCandidateGate.DoNotSaveSignals).ToArbitrary(),
            dns =>
            {
                var extractor = new TurnCandidateExtractor(new FeedbackCandidateAnalyzer());

                // Do-not-save alone (no save) → do-not-save wins.
                var aloneWins = !extractor.SaveIntentFollowsDoNotSave($"please {dns} for now");
                // A save request strictly after the do-not-save → save wins.
                var laterSaveWins = extractor.SaveIntentFollowsDoNotSave($"{dns} but actually save this");
                // A save request before the do-not-save → do-not-save still wins.
                var earlierSaveLoses = !extractor.SaveIntentFollowsDoNotSave($"save this but {dns}");

                return aloneWins && laterSaveWins && earlierSaveLoses;
            });

    // ---- Generators -----------------------------------------------------------

    private static Gen<(string Phrase, string Before, string After)> DoNotSaveWrappedGen()
    {
        var strings = ArbMap.Default.GeneratorFor<string>().Select(s => s ?? string.Empty);
        return from phrase in Gen.Elements(StopHookCandidateGate.DoNotSaveSignals)
               from before in strings
               from after in strings
               select (phrase, before, after);
    }


    private static readonly string[] PayloadKeys =
        ["cwd", "source", "prompt", "assistant_response", "transcript", "transcript_path", "accepted"];

    // A scalar of a random JSON type, so fields sometimes carry the "wrong" type
    // (a number where a string is expected) — the parser must tolerate that too.
    private static Gen<JsonNode?> MixedScalarGen()
    {
        var strings = ArbMap.Default.GeneratorFor<string>()
            .Select(s => s is null ? null : (JsonNode?)JsonValue.Create(s));
        var ints = Gen.Choose(-3, 3).Select(i => (JsonNode?)JsonValue.Create(i));
        var bools = Gen.Elements(new[] { true, false }).Select(b => (JsonNode?)JsonValue.Create(b));
        var nulls = Gen.Choose(0, 0).Select(_ => (JsonNode?)null);
        return Gen.OneOf(new[] { strings, ints, bools, nulls });
    }

    private static Gen<string> ArbitraryPayloadGen()
    {
        var pair =
            from key in Gen.Elements(PayloadKeys)
            from value in MixedScalarGen()
            select (key, value);

        return pair.ListOf().Select(pairs =>
        {
            var obj = new JsonObject();
            foreach (var (key, value) in pairs)
            {
                obj[key] = value;
            }

            return obj.ToJsonString();
        });
    }

    // A payload whose transcript is a random JSONL-ish blob: some valid entries, some
    // malformed lines, varied message-content shapes.
    private static Gen<string> TranscriptPayloadGen()
    {
        var strings = ArbMap.Default.GeneratorFor<string>();

        var line =
            from type in Gen.Elements(new[] { "user", "assistant", "system", "tool" })
            from content in strings
            select new JsonObject
            {
                ["type"] = type,
                ["message"] = new JsonObject { ["content"] = content },
            }.ToJsonString();

        var junk = strings.Select(s => s ?? string.Empty);

        return Gen.OneOf(new[] { line, junk }).ListOf().Select(lines =>
            new JsonObject { ["transcript"] = string.Join("\n", lines) }.ToJsonString());
    }

    private static Gen<string> CommentJsonGen()
    {
        var strings = ArbMap.Default.GeneratorFor<string>();

        var commentObject =
            from body in MixedScalarGen()
            select (JsonNode)new JsonObject { ["body"] = body };

        var element = Gen.OneOf(new[]
        {
            commentObject,
            strings.Select(s => (JsonNode)JsonValue.Create(s ?? string.Empty)),
        });

        var array = element.ListOf().Select(items =>
        {
            var arr = new JsonArray();
            foreach (var item in items)
            {
                arr.Add(item);
            }

            return (JsonNode)arr;
        });

        var wrapped = array.Select(arr => (JsonNode)new JsonObject { ["comments"] = arr });

        return Gen.OneOf(new[] { array, wrapped }).Select(node => node.ToJsonString());
    }
}
