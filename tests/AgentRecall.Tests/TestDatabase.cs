using AgentRecall.Core.Configuration;
using AgentRecall.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentRecall.Tests;

/// <summary>
/// Spins up a service provider backed by a throwaway SQLite database in a
/// unique temp directory. Dispose to remove the database and its folder.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _directory;

    public ServiceProvider Services { get; }
    public AgentRecallOptions Options { get; }

    public TestDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "agentrecall-tests", Guid.NewGuid().ToString("N"));

        Options = new AgentRecallOptions
        {
            DataDirectory = _directory,
            DatabaseFileName = "test.db",
        };

        var services = new ServiceCollection();
        services.AddSingleton(Options);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddAgentRecallPersistence();

        Services = services.BuildServiceProvider();
    }

    /// <summary>Creates a DI scope for resolving scoped services (DbContext, repos).</summary>
    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp directory.
        }
    }
}
