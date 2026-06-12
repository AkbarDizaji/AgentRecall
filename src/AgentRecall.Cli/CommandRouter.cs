using AgentRecall.Core;
using AgentRecall.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentRecall.Cli;

/// <summary>
/// Parses command-line arguments and dispatches to the matching command.
/// Returns the process exit code.
/// </summary>
public static class CommandRouter
{
    public static int Run(string[] args, IServiceProvider services, TextWriter output)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("agentrecall");

        // No arguments behaves like `help`.
        var command = args.Length == 0 ? "help" : args[0];

        switch (command)
        {
            case "--version":
            case "-v":
            case "version":
                output.WriteLine($"{AppInfo.Name} {AppInfo.Version}");
                return 0;

            case "help":
            case "--help":
            case "-h":
                WriteHelp(output);
                return 0;

            case "status":
                // Small demonstration that core services resolve and run.
                var memory = services.GetRequiredService<IMemoryService>();
                logger.LogDebug("Resolved memory service.");
                output.WriteLine(memory.Status());
                return 0;

            default:
                output.WriteLine($"Unknown command: {command}");
                output.WriteLine();
                WriteHelp(output);
                return 1;
        }
    }

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine($"{AppInfo.Name} - local-first memory and learning for AI coding agents");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine($"  {AppInfo.Name} <command> [options]");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  help        Show this help text");
        output.WriteLine("  status      Show the memory subsystem status");
        output.WriteLine("  version     Show the installed version");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --version, -v   Show the installed version");
        output.WriteLine("  --help, -h      Show this help text");
    }
}
