# AgentRecall

[![CI](https://github.com/AkbarDizaji/AgentRecall/actions/workflows/ci.yml/badge.svg)](https://github.com/AkbarDizaji/AgentRecall/actions/workflows/ci.yml)

**A local-first memory for AI coding agents.** AgentRecall captures the feedback
and failures you run into while coding, turns them into reusable technical rules,
and serves those rules back — on the command line or directly to Claude Code over
MCP. Everything stays on your machine: no cloud sync, no web UI, no API keys.

---

## Install

AgentRecall is a .NET global tool. You need the [.NET SDK](https://dotnet.microsoft.com/download)
(8 or newer) installed, then:

```bash
dotnet tool install -g AgentRecall
```

This adds an `agentrecall` command to your PATH. Verify it:

```bash
agentrecall --version
```

> If `agentrecall` isn't found afterward, make sure the .NET tools directory is on
> your PATH (`~/.dotnet/tools` on macOS/Linux, `%USERPROFILE%\.dotnet\tools` on
> Windows), then restart your shell.

Update or remove it later with:

```bash
dotnet tool update -g AgentRecall
dotnet tool uninstall -g AgentRecall
```

---

## Quick start

```bash
# 1. Create the local database (~/.agentrecall/agentrecall.db by default)
agentrecall init

# 2. Teach it something you learned
agentrecall feedback add \
  --task "writing a SQL query" \
  --feedback "use parameterized queries to avoid injection" \
  --tags "sql,security"

# 3. Recall it later
agentrecall search "sql injection"
```

That's the whole loop: **capture → recall.** Everything below is detail on each
step.

---

## Using AgentRecall

### Capture feedback

Record a lesson learned. `--task` and `--feedback` are required; the rest add
context and improve later matching:

```bash
agentrecall feedback add \
  --task "writing a SQL query for user lookup" \
  --feedback "use parameterized queries to avoid injection" \
  --bad-output "string-concatenated SQL" \
  --fixed-output "a parameterized command with @name" \
  --scope-level Repository --scope-value my-repo \
  --tags "security,sql"
```

Each entry is stored as an event and turned into a **rule** you can search for.

### Search and read rules

```bash
agentrecall search "sql injection"          # ranked keyword search
agentrecall search "sql" --scope-value my-repo --limit 5
agentrecall rules list                       # everything you've captured
agentrecall rules show 1                      # full detail for one rule
```

Results are ranked by relevance, rule status, and confidence.

### Curate rules

Rules start as `Pending`. Promote the good ones, retire the rest:

```bash
agentrecall rules approve 1                  # Pending  → Active
agentrecall rules promote 1                  # Active   → Promoted
agentrecall rules supersede 1 2              # replace rule 1 with rule 2
agentrecall rules archive 3                  # hide a rule from search
```

A rule's lifecycle is `Pending → Active → Promoted`, plus `Superseded` and
`Archived`. Superseded and archived rules never show up in search.

### Learn from failures

Point AgentRecall at a build, test, or lint log. Each failure is recorded, and
failures that match an existing rule increase its confidence — repeatedly hitting
the same problem can automatically promote the rule that prevents it:

```bash
agentrecall import build-log ./build.log
agentrecall import test-log ./test.log
agentrecall import lint-log ./lint.log
```

### Command reference

| Command | Description |
| --- | --- |
| `init` | Create the local data directory and database. |
| `feedback add` | Record feedback and extract a rule from it. |
| `search "<query>"` | Search rules by keyword (`--scope-level`, `--scope-value`, `--limit`). |
| `rules list` | List all rules. |
| `rules show <id>` | Show a single rule in detail. |
| `rules approve <id>` | Move a Pending rule to Active. |
| `rules promote <id>` | Promote a rule. |
| `rules supersede <oldId> <newId>` | Replace one rule with another (versioned). |
| `rules archive <id>` | Archive a rule (excluded from search). |
| `import build-log <file>` | Ingest build failures. |
| `import test-log <file>` | Ingest test failures. |
| `import lint-log <file>` | Ingest lint failures. |
| `mcp` | Run the MCP server over stdio (for Claude Code). |
| `status` | Show where data is stored. |
| `help` / `--help` / `-h` | Show usage. |
| `version` / `--version` / `-v` | Show the installed version. |

Run `agentrecall help` any time for the same list.

---

## Use it from Claude Code

AgentRecall ships an MCP server so Claude Code can recall your rules and record
new feedback while you work. Register it once:

```bash
claude mcp add agentrecall agentrecall mcp
```

That exposes three tools to the agent:

| Tool | What it does |
| --- | --- |
| `search_rules` | Find rules relevant to what the agent is doing. |
| `add_feedback` | Record new feedback and create a rule. |
| `get_project_rules` | List the applicable rules for the current scope. |

Each tool returns guidance shaped for an agent: `trigger`, `rule`, `do`,
`do_not`, `reason`, `applies_to`, `confidence`, and `status`. The server speaks
JSON-RPC 2.0 over stdio and writes only protocol messages to stdout (all logs go
to stderr), so it behaves identically whether installed or run from source.

---

## Configuration

AgentRecall reads an optional `agentrecall.json` from the current directory, then
applies environment-variable overrides prefixed with `AGENTRECALL_`.

```json
{
  "AgentRecall": {
    "DataDirectory": "~/.agentrecall",
    "LogLevel": "Information"
  }
}
```

```bash
# Override a setting for a single run
AGENTRECALL_AgentRecall__LogLevel=Debug agentrecall status
```

Data lives in a single SQLite database under `DataDirectory` (default
`~/.agentrecall/agentrecall.db`), so backing up or moving your memory is just
copying that folder.

---

## Building from source

If you'd rather work from the repository instead of installing the published
tool:

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

(`dotnet pack` writes the package to `./nupkg`. If a build is already installed,
use `dotnet tool update --global --add-source ./nupkg AgentRecall`.)

### Project layout

| Project | Purpose |
| --- | --- |
| `AgentRecall.Cli` | Command-line entry point (`agentrecall`) and MCP server. |
| `AgentRecall.Core` | Domain entities, services, and contracts. |
| `AgentRecall.Infrastructure` | Configuration, logging, and EF Core SQLite persistence. |
| `AgentRecall.Tests` | Tests (run against temporary SQLite databases). |

### Releasing

Releases are automated by `.github/workflows/release.yml` on `v*` tags. It
builds, tests, packs, and publishes to NuGet using **Trusted Publishing** (OIDC)
— no API key is stored in the repository.

One-time setup: create a Trusted Publishing policy for the `AgentRecall` package
on NuGet.org (pointing at this repo and `release.yml`), and add a repository
variable `NUGET_USER` with your NuGet.org username.

To cut a release, bump `VersionPrefix` in `Directory.Build.props`, then tag:

```bash
git tag v0.1.0
git push origin v0.1.0
```
