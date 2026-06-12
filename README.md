# AgentRecall

A local-first memory and learning system for AI coding agents.

> **Status: Phase 1 — solution skeleton.** No database, MCP, or embeddings yet.

## Projects

| Project | Purpose |
| --- | --- |
| `AgentRecall.Cli` | Command-line entry point (`agentrecall`). |
| `AgentRecall.Core` | Domain model, options, and service contracts. |
| `AgentRecall.Infrastructure` | Configuration loading and logging setup. |
| `AgentRecall.Tests` | Unit tests. |

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
