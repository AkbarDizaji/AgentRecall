using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture.Judge;

namespace AgentRecall.Core.Services;

/// <summary>
/// The default <see cref="ICaptureJudge"/>. AgentRecall itself calls no model or network; the
/// semantic judgement is produced by the host (the session model) and supplied on the turn
/// payload. This judge simply returns the verdict the host already produced — or <c>null</c>
/// when none was supplied, which the finalizer treats as "judge unavailable → skip".
///
/// A future live provider would implement <see cref="ICaptureJudge"/> to build its own verdict
/// from <see cref="CaptureJudgeInput"/> and would ignore <see cref="CaptureJudgeInput.SuppliedVerdict"/>.
/// </summary>
public sealed class HostSuppliedCaptureJudge : ICaptureJudge
{
    public Task<CaptureJudgeVerdict?> JudgeAsync(CaptureJudgeInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Task.FromResult(input.SuppliedVerdict);
    }
}
