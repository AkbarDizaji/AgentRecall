using Xunit;

namespace AgentRecall.Tests;

/// <summary>
/// Groups the tests that redirect the process-global <see cref="System.Console.In"/>
/// (the CLI hook/finalize paths read stdin). <see cref="System.Console.In"/> is shared
/// across the whole process, so these tests must not run concurrently with one another —
/// otherwise one test's <c>Console.SetIn</c> swaps the reader another is mid-read on.
/// <c>DisableParallelization</c> serialises this collection; the rest of the suite still
/// runs in parallel.
/// </summary>
[CollectionDefinition("ConsoleStdin", DisableParallelization = true)]
public sealed class ConsoleStdinCollection
{
}
