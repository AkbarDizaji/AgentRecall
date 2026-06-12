using AgentRecall.Cli;

using var services = AppHost.Build();
return await CommandRouter.RunAsync(args, services, Console.Out);
