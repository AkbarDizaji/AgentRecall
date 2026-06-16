# AgentRecall

[![CI](https://github.com/AkbarDizaji/AgentRecall/actions/workflows/ci.yml/badge.svg)](https://github.com/AkbarDizaji/AgentRecall/actions/workflows/ci.yml)

**A local-first memory for AI coding agents.** AgentRecall captures the feedback
and failures you run into while coding, turns them into reusable technical rules,
and serves those rules back — on the command line or directly to Claude Code over
MCP. Everything stays on your machine: no cloud sync, no web UI, no API keys.

---

## What it does

AgentRecall is more than a notes file — it actively manages a body of technical
knowledge so the right guidance reaches your agent at the right moment:

- **Turns feedback into structured rules.** Each correction is parsed into a
  readable `trigger`, `rule`, `do`, `do_not`, `reason`, and `applies_to`, then
  validated for quality — not stored as a raw note.
- **Ranks what to surface.** Retrieval scores rules by keyword + semantic +
  domain + task-type + scope match, weighted by confidence, and returns them
  bucketed into **must-follow**, **suggested**, and **warnings** within a token
  budget — not a flat keyword dump.
- **Resolves conflicts automatically.** When rules disagree ("use the repository
  pattern" vs "do not"), the policy engine picks the effective one by scope,
  explicit supersede, priority, recency, then confidence.
- **Learns from failures.** Build, test, and lint logs feed back in; repeatedly
  hitting the same problem raises a rule's confidence and can auto-promote the
  rule that prevents it.
- **Compresses its own memory.** Duplicate, near-duplicate, and overlapping
  rules are detected and merged into one canonical rule, with the originals kept
  as an audit trail.
- **Measures retrieval quality.** A bundled evaluation reports Precision@1/@3
  and Recall@5 and fails CI on a regression, so recall stays trustworthy as the
  rule set grows.
- **Injects deterministically.** A gated Claude Code hook prepends the relevant
  rules to the model's context on every matching prompt — not just when the
  model remembers to ask.

Everything below is the detail behind each of these.

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

Captured rules are **Active** by default, so they apply immediately. Promote the
best, retire the rest:

```bash
agentrecall rules promote 1                  # Active   → Promoted
agentrecall rules approve 1                  # Pending  → Active (if captured as pending)
agentrecall rules supersede 1 2              # replace rule 1 with rule 2
agentrecall rules archive 3                  # hide a rule from search
```

A rule's lifecycle is `Pending → Active → Promoted`, plus `Superseded` and
`Archived`. Superseded and archived rules never show up in search.

Prefer to review before a rule counts? Capture it as pending with
`agentrecall feedback add … --pending`, or make pending the default for every
capture by setting `AutoApproveFeedback` to `false` (see [Configuration](#configuration)).

### Learn from failures

Point AgentRecall at a build, test, or lint log. Each failure is recorded, and
failures that match an existing rule increase its confidence — repeatedly hitting
the same problem can automatically promote the rule that prevents it:

```bash
agentrecall import build-log ./build.log
agentrecall import test-log ./test.log
agentrecall import lint-log ./lint.log
```

### Learn from PR review comments

Reviewer comments are feedback too. Point AgentRecall at a comments file — JSON
from the GitHub CLI or plain text — and each comment that reads as a reusable
correction becomes a **pending rule** (tagged `pr-review`); praise, questions and
nits are skipped:

```bash
gh pr view 42 --json comments > comments.json
agentrecall import pr-comments ./comments.json --task "PR #42: add refunds" --scope-value my-repo
```

Then review what it captured with `agentrecall rules list` and approve the keepers.

### Evaluate retrieval quality

Retrieval is only useful if the right rule comes back for a task. `eval retrieval`
runs a bundled dataset of rules and query scenarios through the ranker in a
throwaway store (your real database is never touched) and reports **Precision@1**,
**Precision@3**, and **Recall@5**:

```bash
agentrecall eval retrieval                 # bundled dataset
agentrecall eval retrieval --dataset ./my-eval.json
```

It exits non-zero when any metric falls below the dataset's baseline, so CI fails
on a retrieval regression. The same check also runs as a unit test.

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
| `import pr-comments <file>` | Capture PR review comments as pending rules (`--task`, `--scope-level`, `--scope-value`, `--tags`). |
| `inject-context "<task>"` | Build agent-ready context (must-follow, warnings, preferred/anti-patterns) for a task. |
| `eval retrieval` | Evaluate retrieval quality against the bundled dataset (`--dataset <path>`); non-zero exit below baseline. |
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

That exposes these tools to the agent:

| Tool | What it does |
| --- | --- |
| `inject_context` | Given a task (and optionally its type, scope, files, changed entities), return the most useful rules — ranked by relevance and confidence, conflict-resolved, and bucketed into **must-follow**, **suggested**, and **warnings**, each with an explanation. |
| `resolve_rules` | When several rules match a task, decide which to follow and which to ignore — resolving direct conflicts and superseded rules. |
| `compress_memory` | Find duplicate, near-duplicate, and overlapping rules and merge each group into one canonical rule (dry run by default; originals are preserved for audit). |
| `get_relevant_context` | Given a task, return the rules to know before starting (keyword-based). |
| `get_project_rules` | The rules that always apply here (project → promoted → active). |
| `get_reminders` | A short checklist of high-signal reminders for a kind of work. |
| `search_rules` | Find rules relevant to a query. |
| `suggest_feedback_candidate` | Detect whether a message is a reusable correction. |
| `capture_feedback` | Save a correction in one step (creates an Active rule by default; `pending=true` to require approval). |
| `add_feedback` | Record feedback with full task/scope context. |
| `import_pr_comments` | Capture PR review comments as pending rules (skips praise/questions/nits). |

Each rule comes back as guidance shaped for an agent: `trigger`, `rule`, `do`,
`do_not`, `reason`, `applies_to`, `confidence`, and `status`. The server speaks
JSON-RPC 2.0 over stdio and writes only protocol messages to stdout (all logs go
to stderr), so it behaves identically whether installed or run from source.

A typical agent loop: call `inject_context` before working (it ranks rules and
flags must-follow guidance and warnings), then run `suggest_feedback_candidate`
on user corrections and `capture_feedback` to remember the good ones — no manual
command needed.

### Make the agent use it automatically

Registering the server only makes the tools *available* — the agent still has to
choose to call them. To make recall part of every task, add a short instruction
to your project's `CLAUDE.md` (or `AGENTS.md`). Drop this in:

```markdown
## Memory (AgentRecall)

The `agentrecall` MCP server holds rules learned from past feedback. Use it:

- **Before** coding, refactoring, reviewing, or debugging, call `inject_context`
  with the task description (and `task_type`, `scope_value`, `file_names`,
  `changed_entities` when known). Follow every **must-follow** rule and heed the
  **warnings** before writing code.
- **When the user corrects you**, call `suggest_feedback_candidate`; if it's a
  reusable lesson, call `capture_feedback` so the same mistake isn't repeated.
- **After a PR review**, pass the reviewer's comments to `import_pr_comments` so
  the actionable ones are remembered as rules.
```

Because `CLAUDE.md` is loaded into the agent's context each session, this turns
"recall the relevant rules first" into default behaviour rather than something
you have to ask for.

### Guarantee it with a hook (deterministic injection)

A `CLAUDE.md` instruction only *nudges* the model — calling an MCP tool is still
its choice. To make rule injection **deterministic**, wire AgentRecall into a
Claude Code [UserPromptSubmit hook](https://docs.claude.com/en/docs/claude-code/hooks).
Hooks are run by Claude Code itself (not the model), so the context is injected
before the model responds, every time the gate matches.

Add this to your project's `.claude/settings.json`:

```json
{
  "hooks": {
    "UserPromptSubmit": [
      { "hooks": [ { "type": "command", "command": "agentrecall hook user-prompt-submit" } ] }
    ]
  }
}
```

**How it works.** On each prompt, Claude Code pipes a small JSON payload (the
prompt text and working directory) to `agentrecall hook user-prompt-submit`. The
command:

1. checks the prompt against a keyword **gate** — non-development prompts are
   skipped entirely (no cost, no output);
2. for a development prompt, retrieves the most relevant rules for the current
   repository (scope = the repo containing the working directory);
3. prints a compact block that Claude Code prepends to the model's context:

```
## AgentRecall Technical Context

Must Follow:
- ...

Warnings:
- ...

Preferred Patterns:
- ...

Anti Patterns:
- ...

Source Rules:
- #12, #4
```

Empty sections are omitted, and only Active/Promoted rules are included. The hook
**never blocks** — if AgentRecall errors, it logs to stderr and injects nothing.

**Configure it** in `agentrecall.json` (no need to touch `settings.json` to tune):

| Setting | Default | Purpose |
| --- | --- | --- |
| `HookEnabled` | `true` | Master switch; `false` makes the hook a no-op. |
| `HookKeywords` | (see below) | Words/phrases that mark a prompt as dev work. |
| `HookMaxRules` | `5` | Maximum rules injected (keeps the block small). |
| `HookIncludePending` | `false` | Whether unapproved (Pending) rules may be injected. |

```json
{
  "AgentRecall": {
    "HookEnabled": true,
    "HookMaxRules": 5,
    "HookIncludePending": false,
    "HookKeywords": [
      "implement", "write", "create", "fix", "debug", "refactor", "review",
      "test", "unit test", "integration test", "api", "endpoint", "repository",
      "service", "controller", "moq", "build", "lint"
    ]
  }
}
```

- **Disable it** — set `"HookEnabled": false`, or remove the hook from
  `settings.json`.
- **Tune gating** — edit `HookKeywords`. Single words match whole words ("api"
  won't fire on "rapid"); multi-word entries match as phrases.
- **Troubleshoot** — run it by hand and inspect the output; failures go to stderr:

  ```bash
  echo '{"prompt":"Write Moq tests for OrderService","cwd":"'"$PWD"'"}' \
    | agentrecall hook user-prompt-submit
  ```

  An empty result means the gate didn't match, the hook is disabled, or no rules
  were relevant. Use `claude --debug` to see hook execution inside Claude Code.

**Verify the flow.** With a Moq rule stored and promoted, the prompt
*"Write Moq tests for OrderService"* drives:

```
User prompt
  → Claude Code fires the UserPromptSubmit hook
  → agentrecall hook user-prompt-submit  (gate matches "write", "moq", "test", "service")
  → inject-context retrieves the Moq rule
  → "## AgentRecall Technical Context …" is prepended to the model context
  → Claude responds with the rule already in view
```

---

## Configuration

AgentRecall reads an optional `agentrecall.json` from the current directory, then
applies environment-variable overrides prefixed with `AGENTRECALL_`.

```json
{
  "AgentRecall": {
    "DataDirectory": "~/.agentrecall",
    "LogLevel": "Information",
    "AutoApproveFeedback": true
  }
}
```

Set `AutoApproveFeedback` to `false` to make every captured rule start as
`Pending` (requiring `rules approve`) instead of going straight to `Active`.

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

To cut a release: bump `VersionPrefix` in `Directory.Build.props`, add an entry
to [`CHANGELOG.md`](CHANGELOG.md) and update `<PackageReleaseNotes>` in
`src/AgentRecall.Cli/AgentRecall.Cli.csproj` (this is what NuGet shows for the
version), then tag:

```bash
git tag v0.2.0
git push origin v0.2.0
```
