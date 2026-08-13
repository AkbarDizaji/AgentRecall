using AgentRecall.Core.Domain;
using AgentRecall.Core.Finalization;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli.Mcp.Tools;

/// <summary>
/// Shared lookup for "is a turn still waiting on its semantic capture judgment?", used by the
/// status surfaces so they answer from recorded state rather than from the previous turn's outcome.
/// </summary>
internal static class JudgmentStatus
{
    /// <summary>
    /// The fresh outstanding judgment request for a chat/directory, or null. Stale rows are ignored
    /// here rather than reported: they are debris from a chat that ended mid-exchange, and the gate
    /// closes them on the next turn it evaluates.
    /// </summary>
    public static async Task<TurnJudgmentRequest?> FindAwaitingAsync(
        IServiceProvider services, string? sessionId, string? cwd, CancellationToken cancellationToken)
    {
        try
        {
            var gate = services.GetRequiredService<ITurnJudgmentGate>();
            var request = await gate.FindOutstandingAsync(sessionId, cwd, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return null;
            }

            var age = DateTimeOffset.UtcNow - request.CreatedAt;
            return age <= TimeSpan.FromMinutes(JudgmentEnforcementPolicy.TurnJudgmentFreshnessMinutes)
                ? request
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Status reporting is best-effort; never fail a status answer over the extra lookup.
            Console.Error.WriteLine($"[agentrecall] judgment-request lookup skipped: {ex.Message}");
            return null;
        }
    }
}
