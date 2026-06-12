using AgentRecall.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentRecall.Infrastructure.Logging;

/// <summary>
/// Creates a console-backed <see cref="ILoggerFactory"/> configured from
/// <see cref="AgentRecallOptions"/>.
/// </summary>
public static class LoggingSetup
{
    /// <summary>
    /// Builds an <see cref="ILoggerFactory"/> whose minimum level is taken from
    /// <paramref name="options"/>. An unrecognized level falls back to Information.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(AgentRecallOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var minLevel = Enum.TryParse<LogLevel>(options.LogLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

        return LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(minLevel)
                .AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = "HH:mm:ss ";
                });
        });
    }
}
