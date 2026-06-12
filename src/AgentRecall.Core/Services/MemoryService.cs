using AgentRecall.Core.Configuration;

namespace AgentRecall.Core.Services;

/// <summary>
/// Default <see cref="IMemoryService"/> implementation. In Phase 1 it holds
/// configuration only and reports that storage is not yet available.
/// </summary>
public sealed class MemoryService : IMemoryService
{
    private readonly AgentRecallOptions _options;

    public MemoryService(AgentRecallOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Status() =>
        $"AgentRecall memory not initialized (no storage in Phase 1). " +
        $"Data directory: {_options.DataDirectory}";
}
