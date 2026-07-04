using System.Security.Cryptography;
using System.Text;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;
using AgentRecall.Core.Seeds;

namespace AgentRecall.Core.CareerImpact;

/// <summary>
/// Default <see cref="ICareerImpactService"/>. Gates the deterministic detector behind the
/// installed <c>career-impact</c> pack and the configured <see cref="CareerImpactMode"/>,
/// persists surfaced candidates idempotently, and answers the on-demand read queries.
/// </summary>
public sealed class CareerImpactService : ICareerImpactService
{
    private readonly CareerImpactDetector _detector;
    private readonly ICareerImpactCandidateRepository _candidates;
    private readonly IRecallRuleRepository _rules;
    private readonly AgentRecallOptions _options;

    public CareerImpactService(
        CareerImpactDetector detector,
        ICareerImpactCandidateRepository candidates,
        IRecallRuleRepository rules,
        AgentRecallOptions options)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<CareerImpactCandidate?> AnalyzeTurnAsync(
        CareerImpactTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mode = _options.ResolvedCareerImpactMode;
        if (mode == CareerImpactMode.Silent)
        {
            return null;
        }

        if (!await IsPackInstalledAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var analysis = _detector.Analyze(new CareerImpactInput
        {
            Prompt = request.Prompt,
            Response = request.Response,
            CapturedRuleTexts = request.CapturedRuleTexts,
        });

        // SignificantOnly stays silent for unremarkable turns; Always surfaces any signal,
        // still bounded by the detector's output. Neither prints for a no-signal turn.
        var surface = mode == CareerImpactMode.Always ? analysis.HasSignal : analysis.IsSignificant;
        if (!surface)
        {
            return null;
        }

        var hash = ComputeHash(request);
        var existing = await _candidates.FindByOperationHashAsync(hash, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var entity = CareerImpactMapping.ToEntity(analysis, request.TurnId, hash, "CareerImpactDetector");
        return await _candidates.AddAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    public Task<CareerImpactCandidate?> GetLastAsync(CancellationToken cancellationToken = default) =>
        _candidates.GetLatestAsync(cancellationToken);

    public Task<CareerImpactCandidate?> GetForTurnAsync(string turnId, CancellationToken cancellationToken = default) =>
        _candidates.FindByTurnAsync(turnId, cancellationToken);

    public async Task<bool> IsPackInstalledAsync(CancellationToken cancellationToken = default)
    {
        var all = await _rules.ListAsync(cancellationToken).ConfigureAwait(false);
        return all.Any(r =>
            r.Source == RuleSource.BuiltInSeed
            && string.Equals(r.SeedPack, CareerImpactSeedPack.Name, StringComparison.OrdinalIgnoreCase)
            && r.Status != RuleStatus.Archived);
    }

    private static string ComputeHash(CareerImpactTurnRequest request)
    {
        var payload = $"{request.TurnId}{request.Prompt}{request.Response}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return "career:" + Convert.ToHexString(bytes);
    }
}
