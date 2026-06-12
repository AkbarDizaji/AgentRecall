using AgentRecall.Cli;

using var services = AppHost.Build();
return CommandRouter.Run(args, services, Console.Out);
