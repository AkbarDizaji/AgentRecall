using AgentRecall.Core.Configuration;
using AgentRecall.Core.Services;
using AgentRecall.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentRecall.Tests;

public class ServiceTests
{
    [Fact]
    public void MemoryService_CanBeInstantiated_AndReportsStatus()
    {
        var options = new AgentRecallOptions();

        IMemoryService service = new MemoryService(options);

        Assert.NotNull(service);
        Assert.False(string.IsNullOrWhiteSpace(service.Status()));
    }

    [Fact]
    public void LoggingSetup_CreatesUsableLoggerFactory()
    {
        var options = new AgentRecallOptions { LogLevel = "Debug" };

        using var factory = LoggingSetup.CreateLoggerFactory(options);
        var logger = factory.CreateLogger("test");

        Assert.NotNull(logger);
        Assert.True(logger.IsEnabled(LogLevel.Debug));
    }
}
