using AgentRecall.Core.Configuration;

namespace AgentRecall.Core.Services;

/// <summary>
/// Default <see cref="IMemoryService"/> implementation. Reports basic status
/// about where AgentRecall keeps its local data.
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
        $"AgentRecall data directory: {_options.DataDirectory}{Environment.NewLine}" +
        $"Database: {_options.DatabasePath}";
}
