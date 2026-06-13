# AgentRecall

[![CI](https://github.com/AkbarDizaji/AgentRecall/actions/workflows/ci.yml/badge.svg)](https://github.com/AkbarDizaji/AgentRecall/actions/workflows/ci.yml)

A local-first memory and learning system for AI coding agents.

AgentRecall captures feedback, turns it into reusable technical rules, retrieves
them by keyword, and exposes everything to Claude Code over MCP. It runs entirely
on your machine — no external embedding providers, web UI, or cloud sync.

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

## Install

AgentRecall is packaged as a .NET global tool, so the `agentrecall` command is
available everywhere once installed.

From NuGet (once published):

```bash
dotnet tool install -g AgentRecall
```

Then:

```bash
agentrecall init
agentrecall status
agentrecall mcp
```

To update or remove:

```bash
dotnet tool update -g AgentRecall
dotnet tool uninstall -g AgentRecall
```

## Development

```bash
dotnet build
dotnet test
```

Run a command without installing:

```bash
dotnet run --project src/AgentRecall.Cli -- <command>
```

## Local tool installation

Pack the tool and install it from the local feed:

```bash
dotnet pack
dotnet tool install --global --add-source ./nupkg AgentRecall
```

`dotnet pack` writes the package to `./nupkg`. If a previous build is already
installed, run `dotnet tool uninstall -g AgentRecall` first (or use
`dotnet tool update --global --add-source ./nupkg AgentRecall`).

## Releasing

Publishing is automated by `.github/workflows/release.yml`, which runs on
`v*` tags (e.g. `v0.1.0`). It builds, tests, packs, and publishes to NuGet
using **Trusted Publishing** (OIDC) — no API key is stored in the repository.

One-time setup:

1. On NuGet.org, create a Trusted Publishing policy for the `AgentRecall`
   package pointing at this repository and the `release.yml` workflow.
2. Add a repository **variable** named `NUGET_USER` set to your NuGet.org
   username (Settings → Secrets and variables → Actions → Variables).

To cut a release, bump `VersionPrefix` in `Directory.Build.props`, then:

```bash
git tag v0.1.0
git push origin v0.1.0
```

The workflow derives the package version from the tag.

## Usage

```bash
agentrecall init
agentrecall status
```

### Commands

| Command | Description |
| --- | --- |
| `init` | Create the local data directory and SQLite database. |
| `feedback add` | Record feedback and extract a pending rule from it. |
| `rules list` | List all rules. |
| `rules show <id>` | Show a single rule in detail. |
| `rules approve <id>` | Move a Pending rule to Active. |
| `rules promote <id>` | Promote a rule. |
| `rules supersede <oldId> <newId>` | Replace one rule with another (versioned). |
| `rules archive <id>` | Archive a rule (excluded from search). |
| `search "<query>"` | Search rules by keyword, ranked by relevance/status/confidence. |
| `import build-log <file>` | Ingest build failures as events. |
| `import test-log <file>` | Ingest test failures as events. |
| `import lint-log <file>` | Ingest lint failures as events. |
| `mcp` | Run the MCP server over stdio (for Claude Code). |
| `help` (also `--help`, `-h`, or no args) | Show usage. |
| `version` (also `--version`, `-v`) | Show the installed version. |
| `status` | Show the memory subsystem status. |

## Rule lifecycle & learning

Rules move through `Pending → Active → Promoted`, and can be `Superseded` or
`Archived`. Superseding records the `SupersededById` link and bumps the
replacement's version. Importing failure logs records each failure as an event;
failures that match an existing rule (by trigger or tag) reinforce it, and a
rule is promoted automatically once its confidence reaches the threshold.
Superseded and archived rules are never reinforced or returned by search.

## MCP server (Claude Code)

`agentrecall mcp` speaks the Model Context Protocol over stdio (newline-delimited
JSON-RPC 2.0). All logging goes to stderr so stdout stays a clean protocol
channel. It exposes three tools:

| Tool | Purpose |
| --- | --- |
| `search_rules` | Search rules relevant to a query (`query`, optional `scope_level`, `scope_value`, `file_path`, `limit`). |
| `add_feedback` | Record feedback and extract a pending rule (`task`, `feedback`, optional `bad_output`, `fixed_output`, `scope_level`, `scope_value`, `tags`). |
| `get_project_rules` | List applicable rules for a scope (optional `scope_level`, `scope_value`). |

Each tool returns agent-facing guidance: `trigger`, `rule`, `do`, `do_not`,
`reason`, `applies_to`, `confidence`, `status`.

Register it with Claude Code (once installed as a global tool):

```bash
claude mcp add agentrecall agentrecall mcp
```

Or, to run from source without installing:

```bash
claude mcp add agentrecall -- dotnet run --project /absolute/path/to/AgentRecall/src/AgentRecall.Cli -- mcp
```

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
