using AgentRecall.Cli;
using AgentRecall.Cli.Setup;

// Self-heal PATH so a globally-installed `agentrecall` is found in future shells —
// a .NET global tool has no post-install hook, so this is the earliest moment we
// can fix it. Idempotent; the notice prints (to stderr) only the first time it
// changes anything. Skipped for the machine-facing commands: `mcp`/`hook` keep a
// clean stdio contract, and `setup` reports the result itself.
var firstArg = args.Length > 0 ? args[0] : string.Empty;
if (firstArg is not ("mcp" or "hook" or "setup"))
{
    var pathResult = PathSetup.Ensure();
    if (pathResult.Outcome == PathSetupOutcome.Added)
    {
        Console.Error.WriteLine(
            $"[agentrecall] Added {pathResult.ToolsDirectory} to your {pathResult.Detail}. " +
            "Open a new terminal so `agentrecall` is found automatically.");
    }
}

using var services = AppHost.Build();
return await CommandRouter.RunAsync(args, services, Console.Out);
