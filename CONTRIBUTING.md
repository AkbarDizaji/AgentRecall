# Contributing to AgentRecall

Thanks for your interest in improving AgentRecall! This guide covers how to set
up the project, the conventions we follow, and how to get a change merged.

## Getting started

You'll need the [.NET SDK](https://dotnet.microsoft.com/download). The projects
target both `net8.0` and `net10.0`, so having a current SDK installed lets you
build and test against either.

```bash
git clone https://github.com/AkbarDizaji/AgentRecall.git
cd AgentRecall

dotnet build
dotnet test

# Run any command without installing
dotnet run --project src/AgentRecall.Cli -- init
```

To build and install your local copy as the global tool:

```bash
dotnet pack
dotnet tool install --global --add-source ./nupkg AgentRecall
```

## Project layout

| Project | Purpose |
| --- | --- |
| `AgentRecall.Cli` | Command-line entry point (`agentrecall`) and MCP server. |
| `AgentRecall.Core` | Domain entities, services, and contracts. |
| `AgentRecall.Infrastructure` | Configuration, logging, and EF Core SQLite persistence. |
| `AgentRecall.Tests` | Tests (run against temporary SQLite databases). |

## Making a change

1. Fork the repository and create a branch off `main`.
2. Make your change, matching the style and structure of the surrounding code.
3. Add or update tests. Tests run against temporary SQLite databases, so they
   don't touch your real data.
4. Run `dotnet build` and `dotnet test` and make sure both pass.
5. Open a pull request describing what the change does and why.

## Conventions

- **Commit messages** describe the change in plain language (for example,
  "Replace the CommandRouter switch with a thin ICommand dispatcher"). Avoid
  generic or numbered messages.
- **Keep documentation in sync.** If you change behavior, update the
  [`README.md`](README.md) and add an entry to [`CHANGELOG.md`](CHANGELOG.md).
- **JSON output is a contract.** The CLI's JSON output uses stable snake_case
  keys; treat changes to it as breaking and call them out in your PR.
- **Tests come with features.** New behavior should land with tests that cover
  it.

## Reporting bugs and requesting features

Open a [GitHub issue](https://github.com/AkbarDizaji/AgentRecall/issues). For
bugs, include the command you ran, what you expected, what happened, and your
operating system and .NET version. For features, describe the problem you're
trying to solve so we can discuss the best fit.

## License

By contributing, you agree that your contributions will be licensed under the
[MIT License](LICENSE) that covers this project.
