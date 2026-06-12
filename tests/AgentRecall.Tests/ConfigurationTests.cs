using AgentRecall.Core.Configuration;
using AgentRecall.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentRecall.Tests;

public class ConfigurationTests
{
    [Fact]
    public void Load_WithNoSources_ReturnsDefaults()
    {
        var options = ConfigurationLoader.Load();

        Assert.NotNull(options);
        Assert.False(string.IsNullOrWhiteSpace(options.DataDirectory));
        Assert.Equal("Information", options.LogLevel);
    }

    [Fact]
    public void Bind_ReadsValuesFromConfigurationSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentRecall:LogLevel"] = "Debug",
                ["AgentRecall:DataDirectory"] = "/tmp/agentrecall-test",
            })
            .Build();

        var options = ConfigurationLoader.Bind(configuration);

        Assert.Equal("Debug", options.LogLevel);
        Assert.Equal("/tmp/agentrecall-test", options.DataDirectory);
    }
}
