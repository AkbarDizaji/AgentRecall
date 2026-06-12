namespace AgentRecall.Core.Abstractions;

/// <summary>
/// Ensures the local AgentRecall database (and its data directory) exist and
/// are ready for use.
/// </summary>
public interface IDatabaseInitializer
{
    /// <summary>
    /// Creates the data directory and database if they do not already exist.
    /// Returns the absolute path to the database file.
    /// </summary>
    Task<string> InitializeAsync(CancellationToken cancellationToken = default);
}
