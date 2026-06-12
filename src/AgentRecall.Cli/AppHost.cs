using AgentRecall.Core.Configuration;
using AgentRecall.Core.Services;
using AgentRecall.Infrastructure.Configuration;
using AgentRecall.Infrastructure.DependencyInjection;
using AgentRecall.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace AgentRecall.Cli;

/// <summary>
/// Composition root: wires configuration, logging, persistence and core
/// services into a <see cref="ServiceProvider"/>.
/// </summary>
public static class AppHost
{
    public static ServiceProvider Build(string? basePath = null)
    {
        var options = ConfigurationLoader.Load(basePath);

        var services = new ServiceCollection();

        services.AddSingleton(options);
        services.AddLogging(builder => LoggingSetup.Configure(builder, options));
        services.AddSingleton<IMemoryService, MemoryService>();
        services.AddAgentRecallPersistence();

        return services.BuildServiceProvider();
    }
}
