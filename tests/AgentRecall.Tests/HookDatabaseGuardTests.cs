using AgentRecall.Cli.Hooks;
using AgentRecall.Core.Abstractions;
using AgentRecall.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Tests that the hooks initialize the database at most once per process per path, so the
/// schema reconciler is not re-run on every prompt in the synchronous hot path.
/// </summary>
public class HookDatabaseGuardTests
{
    private sealed class CountingInitializer : IDatabaseInitializer
    {
        private readonly string _path;
        public int Calls { get; private set; }

        public CountingInitializer(string path) => _path = path;

        public Task<string> InitializeAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_path);
        }
    }

    [Fact]
    public async Task EnsureInitialized_RunsOncePerProcess()
    {
        var path = Path.Combine(Path.GetTempPath(), $"guard-{Guid.NewGuid():N}.db");
        HookDatabaseGuard.Reset(path);

        var initializer = new CountingInitializer(path);
        var services = new ServiceCollection()
            .AddSingleton(new AgentRecallOptions { DataDirectory = Path.GetDirectoryName(path)!, DatabaseFileName = Path.GetFileName(path) })
            .AddSingleton<IDatabaseInitializer>(initializer)
            .BuildServiceProvider();

        await HookDatabaseGuard.EnsureInitializedAsync(services);
        await HookDatabaseGuard.EnsureInitializedAsync(services);
        await HookDatabaseGuard.EnsureInitializedAsync(services);

        Assert.Equal(1, initializer.Calls);
    }
}
