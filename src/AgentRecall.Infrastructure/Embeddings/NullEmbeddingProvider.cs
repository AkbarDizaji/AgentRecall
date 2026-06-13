using AgentRecall.Core.Abstractions;

namespace AgentRecall.Infrastructure.Embeddings;

/// <summary>
/// Placeholder <see cref="IEmbeddingProvider"/> that produces no embeddings.
/// Keeps the search pipeline keyword-only until a real provider (local model or
/// external API) is integrated. No external services are contacted.
/// </summary>
public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public bool IsAvailable => false;

    public int Dimensions => 0;

    public ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "No embedding provider is configured. Embeddings are not yet enabled.");
}
