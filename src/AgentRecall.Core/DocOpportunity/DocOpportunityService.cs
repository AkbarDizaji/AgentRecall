using System.Security.Cryptography;
using System.Text;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Capture.Judge;
using AgentRecall.Core.Configuration;
using AgentRecall.Core.Domain;

namespace AgentRecall.Core.DocOpportunity;

/// <summary>
/// Default <see cref="IDocOpportunityService"/>. Gates the host-supplied judge behind the
/// configured <see cref="DocOpportunityMode"/>, validates and persists offered candidates
/// idempotently, and answers the on-demand read queries. Unlike <c>TurnFinalizer</c>'s judge
/// there is no multi-branch decision tree afterward — the judge already returns a final
/// offer-or-skip verdict, so the judge call lives directly in this service.
/// </summary>
public sealed class DocOpportunityService : IDocOpportunityService
{
    private readonly IDocOpportunityJudge _judge;
    private readonly IDocOpportunityCandidateRepository _candidates;
    private readonly AgentRecallOptions _options;

    public DocOpportunityService(
        IDocOpportunityJudge judge,
        IDocOpportunityCandidateRepository candidates,
        AgentRecallOptions options)
    {
        _judge = judge ?? throw new ArgumentNullException(nameof(judge));
        _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<DocOpportunityCandidate?> AnalyzeTurnAsync(
        DocOpportunityTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_options.ResolvedDocOpportunityMode == Domain.DocOpportunityMode.Off)
        {
            return null;
        }

        var verdict = await _judge.JudgeAsync(new DocOpportunityJudgeInput
        {
            UserPrompt = request.Prompt,
            AssistantSummary = request.Response,
            Source = request.Source,
            ScopeLevel = request.ScopeLevel,
            ScopeValue = request.ScopeValue,
            SuppliedVerdict = request.SuppliedVerdict,
        }, cancellationToken).ConfigureAwait(false);

        if (verdict is null)
        {
            // No verdict: the judge is unavailable. Nothing offered — never a keyword fallback.
            return null;
        }

        var validation = DocOpportunityValidator.Validate(verdict);
        if (!validation.IsValid || verdict.Decision != DocOpportunityDecision.Offer)
        {
            return null;
        }

        var hash = ComputeHash(request);
        var existing = await _candidates.FindByOperationHashAsync(hash, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var entity = DocOpportunityMapping.ToEntity(verdict, request.TurnId, hash);
        return await _candidates.AddAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    public Task<DocOpportunityCandidate?> GetLastAsync(CancellationToken cancellationToken = default) =>
        _candidates.GetLatestAsync(cancellationToken);

    public Task<DocOpportunityCandidate?> GetForTurnAsync(string turnId, CancellationToken cancellationToken = default) =>
        _candidates.FindByTurnAsync(turnId, cancellationToken);

    public async Task<DocOpportunityCandidate?> MarkWrittenAsync(
        int candidateId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var candidate = await _candidates.GetAsync(candidateId, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }

        candidate.Status = DocOpportunityStatus.Written;
        candidate.WrittenPath = path;
        return await _candidates.UpdateAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeHash(DocOpportunityTurnRequest request)
    {
        var payload = $"{request.TurnId}{request.Prompt}{request.Response}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return "doc:" + Convert.ToHexString(bytes);
    }
}
