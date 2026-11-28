using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Activity;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Hooks;
using AgentRecall.Core.Text;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Hooks;

/// <summary>
/// The outcome of a UserPromptSubmit hook run, split like <see cref="PreToolUseInjection"/>:
/// <see cref="AdditionalContext"/> is the rule block the model reads, and
/// <see cref="SystemMessage"/> is the status line shown to the user. Printed as plain text, the
/// status line went into the model's context with the rules and the user never saw it.
/// </summary>
public readonly record struct PromptInjection(string AdditionalContext, string? SystemMessage)
{
    public static readonly PromptInjection None = new(string.Empty, null);

    /// <summary>True when there is nothing to inject.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(AdditionalContext);
}

/// <summary>
/// Runs the gated UserPromptSubmit hook: parses Claude Code's hook payload, decides
/// whether the prompt is development-related, retrieves the most relevant rules, and
/// returns a compact context block to inject. It never throws — on any failure it
/// logs to <paramref name="diagnostics"/> and returns empty so Claude is never blocked.
/// </summary>
public static class UserPromptSubmitHook
{
    /// <summary>The status line and the rule block as one text, the status line first.</summary>
    public static async Task<string> RunAsync(
        string? hookInputJson,
        IServiceProvider services,
        TextWriter diagnostics,
        CancellationToken cancellationToken = default)
    {
        var injection = await RunForHostAsync(hookInputJson, services, diagnostics, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(injection.SystemMessage)
            ? injection.AdditionalContext
            : injection.SystemMessage + "\n\n" + injection.AdditionalContext;
    }

    /// <summary>The rule block for the model and the status line for the user, kept apart.</summary>
    public static async Task<PromptInjection> RunForHostAsync(
        string? hookInputJson,
        IServiceProvider services,
        TextWriter diagnostics,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = services.GetRequiredService<AgentRecallOptions>();
            if (!options.HookEnabled)
            {
                return PromptInjection.None;
            }

            if (!TryReadPrompt(hookInputJson, out var prompt, out var cwd, out var sessionId))
            {
                return PromptInjection.None;
            }

            // A prompt the keyword gate turns away still gets the standing rules: they apply to
            // every task, so they cannot hinge on the prompt containing an English keyword.
            await using var scope = services.CreateAsyncScope();
            var standingOnly = !PromptGate.IsRelevant(prompt, options.HookKeywords)
                && !await MentionsGlossaryAsync(scope.ServiceProvider, prompt, options, cancellationToken).ConfigureAwait(false);

            var repository = RepositoryName(cwd);
            var request = new ContextRequest
            {
                Task = prompt,
                ScopeLevel = repository is null ? null : ScopeLevel.Repository,
                ScopeValue = repository,
                Limit = options.HookMaxRules,
                ExtendedLimit = options.HookExtendedMaxRules,
                ExtendedScoreFloor = options.HookExtendedScoreFloor,
                IncludePending = options.HookIncludePending,
                PendingCap = options.HookPendingCap,
                // Rules surfaced to the agent count as retrievals for learning reports.
                RecordUsage = true,
                SessionId = sessionId,
                StandingOnly = standingOnly,
            };

            // Initialize the database once per process; the reconciler is wasted work on
            // every subsequent prompt. Usage recording itself is a single batched write.
            await HookDatabaseGuard.EnsureInitializedAsync(scope.ServiceProvider, cancellationToken)
                .ConfigureAwait(false);

            var context = scope.ServiceProvider.GetRequiredService<IContextInjectionService>();
            var result = await context.BuildContextAsync(request, cancellationToken).ConfigureAwait(false);

            var formatted = HookContextFormatter.Format(result);
            if (string.IsNullOrEmpty(formatted))
            {
                // Nothing was injected: no notice, no activity (never spam on a no-op).
                return PromptInjection.None;
            }

            // Persist the activity for the human-visible log. Verbose detail is stored
            // but never injected — the hook output only carries a compact summary line.
            // Stamp the deterministic turn id so the rules used here can later be joined
            // with this turn's capture into one end-of-turn summary.
            var recorder = scope.ServiceProvider.GetRequiredService<IActivityRecorder>();
            var turnId = TurnCorrelation.Compute(cwd, prompt);
            var notice = ActivityNoticeFactory.ForContextFetched(result, "hook");
            if (notice is not null)
            {
                await recorder.RecordAsync(notice with { TurnId = turnId }, cancellationToken).ConfigureAwait(false);
            }

            var conflictNotice = ActivityNoticeFactory.ForConflictResolved(result.Conflicts, "hook");
            if (conflictNotice is not null)
            {
                await recorder.RecordAsync(conflictNotice with { TurnId = turnId }, cancellationToken).ConfigureAwait(false);
            }

            // The user-facing notice is a single compact line (never the detail bullets). It also
            // says how many rules this chat has been given so far, since one prompt's count alone
            // hides how much the context has accumulated.
            var compact = notice is null
                ? null
                : ActivityNoticeRenderer.RenderCompact(notice, options.ResolvedHookNoticeLevel);
            if (!string.IsNullOrEmpty(compact) && !string.IsNullOrWhiteSpace(sessionId))
            {
                var inChat = await RulesInChatAsync(scope.ServiceProvider, sessionId, cancellationToken).ConfigureAwait(false);
                compact = compact.TrimEnd('.') + $" · **{inChat} {Plural.Of(inChat, "rule")} in this chat**.";
            }

            return new PromptInjection(formatted, string.IsNullOrEmpty(compact) ? null : compact);
        }
        catch (Exception ex)
        {
            // Never block the prompt; surface the failure on stderr only.
            diagnostics.WriteLine($"[agentrecall] hook skipped: {ex.Message}");
            return PromptInjection.None;
        }
    }

    /// <summary>Distinct rules injected into this chat so far, this prompt's included.</summary>
    private static async Task<int> RulesInChatAsync(
        IServiceProvider scopedServices,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var records = await scopedServices.GetRequiredService<IRetrievalRecordRepository>()
            .ListAsync(cancellationToken).ConfigureAwait(false);

        return records
            .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal))
            .SelectMany(r => IdList.Parse(r.RuleIds))
            .Distinct()
            .Count();
    }

    /// <summary>
    /// True when a prompt the keyword gate turned away still names a glossary term, or passes
    /// the gate once its words are mapped to English. The gate's keywords are English, so
    /// without this a prompt in another language could never reach the rules; a term the user
    /// put in the glossary is one they care about, so naming it is reason enough to look.
    /// Only consulted for prompts the gate rejected, so an English prompt pays nothing extra.
    /// </summary>
    private static async Task<bool> MentionsGlossaryAsync(
        IServiceProvider scopedServices,
        string prompt,
        AgentRecallOptions options,
        CancellationToken cancellationToken)
    {
        await HookDatabaseGuard.EnsureInitializedAsync(scopedServices, cancellationToken).ConfigureAwait(false);
        var glossary = await scopedServices.GetRequiredService<IGlossaryTermRepository>()
            .ListAsync(cancellationToken).ConfigureAwait(false);

        return Glossary.Matches(prompt, glossary).Count > 0
            || PromptGate.IsRelevant(Glossary.Translate(prompt, glossary), options.HookKeywords);
    }

    private static bool TryReadPrompt(string? json, out string prompt, out string? cwd, out string? sessionId)
    {
        prompt = string.Empty;
        cwd = null;
        sessionId = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        prompt = root?["prompt"]?.GetValue<string>() ?? string.Empty;
        cwd = root?["cwd"]?.GetValue<string>();

        // The chat id, so a rule this chat has already read can be repeated as a reminder.
        sessionId = root?["session_id"]?.GetValue<string>();

        return !string.IsNullOrWhiteSpace(prompt);
    }

    /// <summary>Repository name = the nearest ancestor with a .git, else the cwd's name.</summary>
    private static string? RepositoryName(string? cwd) => TurnPayload.RepositoryScopeName(cwd);
}
