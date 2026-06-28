using Microsoft.Extensions.Logging;

namespace AgentRecall.Cli.Commands;

/// <summary>
/// A single CLI command group (e.g. <c>feedback</c>, <c>rules</c>, <c>search</c>). The
/// dispatcher (<see cref="CommandRouter"/>) maps the first argument to one of these and
/// runs it, so adding or changing a command is local to its handler rather than a branch
/// in a large switch.
/// </summary>
public interface ICommand
{
    /// <summary>
    /// Runs the command. <paramref name="args"/> are the arguments after the command name.
    /// Returns the process exit code.
    /// </summary>
    Task<int> ExecuteAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken);
}

/// <summary>
/// An <see cref="ICommand"/> backed by a delegate, so a handler method can be registered
/// without a dedicated class. Keeps the dispatcher a thin lookup table.
/// </summary>
public sealed class DelegateCommand : ICommand
{
    private readonly Func<string[], IServiceProvider, TextWriter, ILogger, CancellationToken, Task<int>> _run;

    public DelegateCommand(Func<string[], IServiceProvider, TextWriter, ILogger, CancellationToken, Task<int>> run)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public Task<int> ExecuteAsync(
        string[] args,
        IServiceProvider services,
        TextWriter output,
        ILogger logger,
        CancellationToken cancellationToken) =>
        _run(args, services, output, logger, cancellationToken);
}
