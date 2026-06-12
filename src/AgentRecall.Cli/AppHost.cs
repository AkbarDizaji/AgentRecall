using AgentRecall.Core.Configuration;
using AgentRecall.Core.Services;
using AgentRecall.Infrastructure.Configuration;
using AgentRecall.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentRecall.Cli;

/// <summary>
/// Composition root: wires configuration, logging and core services into a
/// <see cref="ServiceProvider"/>. Kept deliberately small for Phase 1.
/// </summary>
public static class AppHost
{
    public static ServiceProvider Build(string? basePath = null)
    {
        var options = ConfigurationLoader.Load(basePath);

        var services = new ServiceCollection();

        services.AddSingleton(options);
        services.AddSingleton<ILoggerFactory>(_ => LoggingSetup.CreateLoggerFactory(options));
        services.AddLogging();
        services.AddSingleton<IMemoryService, MemoryService>();

        return services.BuildServiceProvider();
    }
}
