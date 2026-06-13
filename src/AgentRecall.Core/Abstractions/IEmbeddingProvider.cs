namespace AgentRecall.Core.Abstractions;

/// <summary>
/// Produces vector embeddings for text. Phase 4 ships only a no-op provider so
/// the search pipeline can blend semantic similarity once a real provider
/// (local model or external API) is wired in.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>
    /// Whether this provider can actually produce embeddings. When false, the
    /// search service runs keyword-only and never calls <see cref="EmbedAsync"/>.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>The dimensionality of produced vectors (0 when unavailable).</summary>
    int Dimensions { get; }

    /// <summary>Embeds a single piece of text into a dense vector.</summary>
    ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
