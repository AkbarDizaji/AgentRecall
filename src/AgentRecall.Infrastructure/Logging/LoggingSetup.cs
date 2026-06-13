using AgentRecall.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

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
    public static ILoggerFactory CreateLoggerFactory(AgentRecallOptions options) =>
        LoggerFactory.Create(builder => Configure(builder, options));

    /// <summary>
    /// Configures an <see cref="ILoggingBuilder"/> from <paramref name="options"/>:
    /// single-line console output at the configured minimum level (falling back
    /// to Information for an unrecognized value). Used by both the standalone
    /// factory and the DI container.
    /// </summary>
    public static void Configure(ILoggingBuilder builder, AgentRecallOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        var minLevel = Enum.TryParse<LogLevel>(options.LogLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

        builder
            .SetMinimumLevel(minLevel)
            // EF Core's per-command logging is verbose for a CLI; keep only warnings+.
            .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning)
            .AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });

        // Route all logs to stderr so stdout stays clean — required for the MCP
        // stdio transport, which uses stdout as the protocol channel.
        builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    }
}
