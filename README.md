# AgentRecall

A local-first memory and learning system for AI coding agents.

> **Status: Phase 3 — feedback capture & rule extraction.** Feedback is stored
> as events and converted into pending rules by a rule-based extractor. No LLM
> integration, vector search, or MCP yet.

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
| `feedback add` | Record feedback and extract a pending rule from it. |
| `rules list` | List all rules. |
| `rules show <id>` | Show a single rule in detail. |
| `help` (also `--help`, `-h`, or no args) | Show usage. |
| `version` (also `--version`, `-v`) | Show the installed version. |
| `status` | Show the memory subsystem status. |

### Capturing feedback

Feedback is stored as a `RecallEvent` and converted into a `Pending`
`RecallRule` by a deterministic, rule-based extractor (no LLM yet).

```bash
agentrecall feedback add \
  --task "write a SQL query for user lookup" \
  --feedback "use parameterized queries to avoid injection" \
  --bad-output "string-concatenated SQL" \
  --fixed-output "a parameterized command with @name" \
  --scope-level Repository --scope-value AgentRecall \
  --tags "security,sql"
```

`--task` and `--feedback` are required; the rest are optional.

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
