using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Context;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Hooks;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Hooks;

/// <summary>
/// Runs the gated UserPromptSubmit hook: parses Claude Code's hook payload, decides
/// whether the prompt is development-related, retrieves the most relevant rules, and
/// returns a compact context block to inject. It never throws — on any failure it
/// logs to <paramref name="diagnostics"/> and returns empty so Claude is never blocked.
/// </summary>
public static class UserPromptSubmitHook
{
    public static async Task<string> RunAsync(
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
                return string.Empty;
            }

            if (!TryReadPrompt(hookInputJson, out var prompt, out var cwd))
            {
                return string.Empty;
            }

            if (!PromptGate.IsRelevant(prompt, options.HookKeywords))
            {
                return string.Empty;
            }

            var repository = RepositoryName(cwd);
            var request = new ContextRequest
            {
                Task = prompt,
                ScopeLevel = repository is null ? null : ScopeLevel.Repository,
                ScopeValue = repository,
                Limit = options.HookMaxRules,
                IncludePending = options.HookIncludePending,
            };

            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>()
                .InitializeAsync(cancellationToken).ConfigureAwait(false);

            var context = scope.ServiceProvider.GetRequiredService<IContextInjectionService>();
            var result = await context.BuildContextAsync(request, cancellationToken).ConfigureAwait(false);

            return HookContextFormatter.Format(result);
        }
        catch (Exception ex)
        {
            // Never block the prompt; surface the failure on stderr only.
            diagnostics.WriteLine($"[agentrecall] hook skipped: {ex.Message}");
            return string.Empty;
        }
    }

    private static bool TryReadPrompt(string? json, out string prompt, out string? cwd)
    {
        prompt = string.Empty;
        cwd = null;

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
        return !string.IsNullOrWhiteSpace(prompt);
    }

    /// <summary>Repository name = the nearest ancestor with a .git, else the cwd's name.</summary>
    private static string? RepositoryName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return null;
        }

        try
        {
            var dir = new DirectoryInfo(cwd);
            for (var current = dir; current is not null; current = current.Parent)
            {
                if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                {
                    return current.Name;
                }
            }

            return dir.Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
