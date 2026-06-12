# AgentRecall

A local-first memory and learning system for AI coding agents.

> **Status: Phase 2 — local SQLite persistence.** Entities, repositories, and
> database initialization are in place. No vector search or MCP yet.

## Projects

| Project | Purpose |
| --- | --- |
| `AgentRecall.Cli` | Command-line entry point (`agentrecall`). |
| `AgentRecall.Core` | Domain entities, options, and repository/initializer contracts. |
| `AgentRecall.Infrastructure` | Configuration, logging, and EF Core SQLite persistence. |
| `AgentRecall.Tests` | Unit tests (use temporary SQLite databases). |

## Persistence

A local SQLite database is stored under the data directory (default
`~/.agentrecall/agentrecall.db`). It holds three entities — `RecallRule`,
`RecallEvent`, and `RecallScope` — accessed through repositories. Run
`agentrecall init` to create the directory and schema.

## Requirements

- .NET 8 SDK or newer (developed against .NET 10).

## Build & test

```bash
dotnet build
dotnet test
```

## Run

```bash
dotnet run --project src/AgentRecall.Cli -- <command>
```

### Commands

| Command | Description |
| --- | --- |
| `init` | Create the local data directory and SQLite database. |
| `help` (also `--help`, `-h`, or no args) | Show usage. |
| `version` (also `--version`, `-v`) | Show the installed version. |
| `status` | Show the memory subsystem status. |

## Configuration

Configuration is loaded from an optional `agentrecall.json` in the working
directory, then overridden by environment variables prefixed with
`AGENTRECALL_`.

```json
{
  "AgentRecall": {
    "DataDirectory": "~/.agentrecall",
    "LogLevel": "Information"
  }
}
```

Example environment override:

```bash
AGENTRECALL_AgentRecall__LogLevel=Debug dotnet run --project src/AgentRecall.Cli -- status
```
