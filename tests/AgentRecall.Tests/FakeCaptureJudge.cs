using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture.Judge;

namespace AgentRecall.Tests;

/// <summary>
/// A test double for <see cref="ICaptureJudge"/>. The model's verdict is supplied directly, so
/// the semantic-capture path is exercised offline and deterministically — no LLM, no network.
/// A <c>null</c> verdict (the default) models the judge being unavailable.
/// </summary>
public sealed class FakeCaptureJudge : ICaptureJudge
{
    private readonly Func<CaptureJudgeInput, CaptureJudgeVerdict?> _respond;

    /// <summary>The last input the finalizer handed the judge, for asserting the built payload.</summary>
    public CaptureJudgeInput? LastInput { get; private set; }

    public FakeCaptureJudge(CaptureJudgeVerdict? verdict = null)
        : this(_ => verdict)
    {
    }

    public FakeCaptureJudge(Func<CaptureJudgeInput, CaptureJudgeVerdict?> respond)
    {
        _respond = respond ?? throw new ArgumentNullException(nameof(respond));
    }

    public Task<CaptureJudgeVerdict?> JudgeAsync(CaptureJudgeInput input, CancellationToken cancellationToken = default)
    {
        LastInput = input;
        return Task.FromResult(_respond(input));
    }
}
