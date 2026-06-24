namespace AgentRecall.Core.Dna;

/// <summary>
/// Summarises the "engineering personality" of a repository into an
/// onboarding-ready report: core principles, conventions, testing and
/// architecture patterns, error-handling and security rules, common mistakes,
/// agent warnings, and stale/risky knowledge. Local-first and deterministic.
/// </summary>
public interface IProjectDnaService
{
    /// <summary>Builds the Project DNA report for the given options.</summary>
    Task<ProjectDnaReport> GenerateAsync(ProjectDnaOptions options, CancellationToken cancellationToken = default);
}
