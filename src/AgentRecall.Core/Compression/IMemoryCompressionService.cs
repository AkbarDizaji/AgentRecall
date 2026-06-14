namespace AgentRecall.Core.Compression;

/// <summary>
/// Detects duplicate, near-duplicate and overlapping rules (including repeated
/// corrections) and compresses each group into a single canonical rule, while
/// preserving the original rules and feedback for audit.
/// </summary>
public interface IMemoryCompressionService
{
    /// <summary>
    /// Detects compression candidates and projects the resulting statistics
    /// without changing anything.
    /// </summary>
    Task<CompressionAnalysis> AnalyzeAsync(CompressionOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compresses every detected group: creates a canonical rule, supersedes the
    /// originals (which are preserved, along with their feedback), and records an
    /// audit event per group.
    /// </summary>
    Task<CompressionResult> CompressAsync(CompressionOptions options, CancellationToken cancellationToken = default);
}
