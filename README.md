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
  budget — not a flat keyword dump. Here "semantic" means a deterministic,
  built-in concept graph (e.g. *refund* relates to *money*), **not** vector
  embeddings — ranking is keyword + concept based out of the box. Embedding-based
  similarity is an optional extension point that stays off until an
  `IEmbeddingProvider` is configured (the default contributes nothing); see
  [Search ranking](#search-ranking).
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

This installs `agentrecall` into the .NET tools directory (`~/.dotnet/tools` on
macOS/Linux, `%USERPROFILE%\.dotnet\tools` on Windows). Verify it:

```bash
agentrecall --version
```

**PATH is handled for you.** A .NET global tool has no post-install step, so a
fresh install often isn't on `PATH` in your current shell — which is why
`agentrecall` can report "command not found" right after a successful install.
The first time AgentRecall runs it adds its directory to your PATH permanently
(your shell profile on macOS/Linux, the user `PATH` on Windows) and prints a
one-time notice; open a new terminal and it's found automatically. If the very
first invocation is the one that can't be found, either open a new terminal, or
run it once by full path / via the bootstrap below.

Bootstrap install (installs **and** fixes PATH in one step):

```bash
# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/AkbarDizaji/AgentRecall/main/scripts/install.sh | bash
```

```powershell
# Windows (PowerShell)
iwr -useb https://raw.githubusercontent.com/AkbarDizaji/AgentRecall/main/scripts/install.ps1 | iex
```

You can also fix PATH any time with `agentrecall setup`.

Update or remove it later with:

```bash
dotnet tool update -g AgentRecall
dotnet tool uninstall -g AgentRecall
```

### Dev containers

A global tool installs under `~/.dotnet/tools`, which lives on the container
filesystem and is wiped by "Rebuild Container" — so a manual `dotnet tool install`
disappears on every rebuild. Run this once from your project root to make the
install survive rebuilds:

```bash
agentrecall devcontainer init
```

It writes `.devcontainer/agentrecall-post-create.sh`, which reinstalls AgentRecall
from NuGet, persists the database on a named Docker volume, and re-registers the
MCP server on every create/rebuild. The script logs each step and, on failure,
names the command that aborted it. If your project has no `devcontainer.json`,
one is generated and wired to run the script; if you already have one, it's left
untouched and the exact keys to merge in are printed — the JSONC is never
rewritten.

It also wires the **`UserPromptSubmit` hook** into `.claude/settings.json` (see
[Guarantee it with a hook](#guarantee-it-with-a-hook-deterministic-injection)),
so relevant rules are injected automatically on every prompt rather than only
when the agent decides to call an MCP tool, and appends the **`CLAUDE.md`
guidance block** so the agent recalls rules and captures accepted PR comments as
Active rules by default. Existing settings, `CLAUDE.md` content, and JSONC are
merged or left untouched — never overwritten — and a re-run is a no-op.

> **PATH note.** A global tool lives in `~/.dotnet/tools`, and VS Code often opens
> integrated terminals as non-login shells that don't read `~/.profile`, so
> `agentrecall` can be installed yet "not found". The generated manifest adds the
> directory via `remoteEnv`, and the setup script also appends it to `~/.bashrc`;
> for an existing `devcontainer.json`, add
> `"remoteEnv": { "PATH": "${containerEnv:PATH}:/home/vscode/.dotnet/tools" }`.

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

### Search ranking

Ranking is **keyword-based out of the box** — there is no semantic vector search
unless you wire one up. `search` scores rules by weighted term matches across the
trigger, tags, rule text, mistake, and technical context, blended with status and
confidence. Context injection adds a deterministic, built-in **concept graph** so a
task about *refunds* can surface a *money* rule with no shared words; this is rule-based
relatedness, not embeddings.

The pipeline has an `IEmbeddingProvider` extension point for hybrid (keyword + vector)
search, but the only provider shipped is a no-op (`IsAvailable == false`), so **no
embeddings are computed and no external service is contacted** by default. Until a real
provider is configured, semantic similarity contributes nothing to the score — ranking
stays keyword + concept based and fully local and deterministic.

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

If the comments have already been **accepted** — you acted on them, so they're not
guesses to vet — add `--accepted` and they're recorded as **Active** rules
straight away (the MCP tool takes the same `accepted: true`). The scaffolded
`CLAUDE.md` guidance tells the agent to do this on its own: when you ask it to
apply what a review comment says, it captures that comment as an Active rule
without being asked.

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
| `setup` | Ensure the .NET tools directory is on your PATH (runs automatically on first use). |
| `devcontainer init` | Scaffold dev container wiring so AgentRecall reinstalls on every rebuild (optional `[path]`). |
| `feedback add` | Record feedback and extract a rule from it. |
| `search "<query>"` | Search rules by keyword (`--scope-level`, `--scope-value`, `--limit`). |
| `rules list` | List all rules (`--status <status>`, e.g. `Pending`). |
| `rules show <id>` | Show a single rule in detail. |
| `rules approve <id>` | Move a Pending rule to Active. |
| `rules promote <id>` | Promote a rule. |
| `rules supersede <oldId> <newId>` | Replace one rule with another (versioned). |
| `rules archive <id>` | Archive a rule (excluded from search). |
| `import build-log <file>` | Ingest build failures. |
| `import test-log <file>` | Ingest test failures. |
| `import lint-log <file>` | Ingest lint failures. |
| `import pr-comments <file>` | Capture PR review comments as rules (`--task`, `--scope-level`, `--scope-value`, `--tags`; `--accepted` records them Active instead of pending). |
| `inject-context "<task>"` | Build agent-ready context (must-follow, warnings, preferred/anti-patterns) for a task. |
| `dna` | Summarise the repo's engineering personality for onboarding (`--markdown`, `--json`, `--top <n>`, `--scope-level`, `--scope-value`, `--output <file>`). |
| `eval retrieval` | Evaluate retrieval quality against the bundled dataset (`--dataset <path>`); non-zero exit below baseline. |
| `activity last` | Show the latest AgentRecall activity notice (`--json`). |
| `activity list` | Show recent activity notices, newest first (`--limit <n>`, `--json`). |
| `mcp` | Run the MCP server over stdio (for Claude Code). |
| `status` | Show where data is stored. |
| `help` / `--help` / `-h` | Show usage. |
| `version` / `--version` / `-v` | Show the installed version. |

Run `agentrecall help` any time for the same list.

---

## Project DNA

**Project DNA** distils everything AgentRecall has learned about a repository into a
single, onboarding-ready summary of its *engineering personality* — the conventions,
patterns, risks, and recurring lessons that explain how the project actually works.
The goal is to get a new developer (or a new AI coding agent) productive in **under
five minutes**.

It reads the same local data the rest of AgentRecall uses — active and promoted
rules, how often each rule is retrieved, mined lesson candidates, accepted lifecycle
recommendations, outcomes, and detected conflicts — and organises it into fixed
sections:

- **Core Principles** — the highest-confidence, most broadly applicable lessons.
- **Repository Conventions** — repo-specific "when X, do Y" rules.
- **Testing Patterns** — testing rules and common testing mistakes.
- **Architecture Patterns** — architecture and design preferences.
- **Error Handling** — exceptions, `Result<T>`, validation, domain failures.
- **Feature Gates / Authorization / Security** — gates, permissions, auth, access control.
- **Common Mistakes** — frequently corrected or mined lessons.
- **Agent Warnings** — high-impact anti-patterns to avoid.
- **Stale or Risky Knowledge** — low-confidence or conflict-prone guidance to review.

### How it differs from search and reports

Search answers "which rules match *this* task?" and the `report` commands give you
*metrics* (counts, growth, staleness). Project DNA is neither: it's a **curated,
deterministic narrative** of the whole corpus, ranked so the most trustworthy and
broadly useful guidance rises to the top — Promoted over Active over Pending, then by
confidence, retrieval frequency, recency, outcome evidence, and lifecycle signals.
There are **no LLM calls, no embeddings, and no external services** — the same inputs
always produce the same output.

### Generate it

```bash
agentrecall dna                                          # human-readable summary
agentrecall dna --markdown                               # Markdown for onboarding docs
agentrecall dna --json                                   # stable, structured JSON
agentrecall dna --top 10                                 # more items per section
agentrecall dna --scope-level Repository --scope-value my-repo   # one repo only
agentrecall dna --markdown --output PROJECT_DNA.md       # write straight to a file
```

The Markdown output is designed to drop straight into a `PROJECT_DNA.md`,
`CONTRIBUTING.md`, or a wiki page. The JSON output is stable (snake_case keys —
`generated_at`, `scope`, `sections`, `items`, `rule_ids`, `confidence`, `evidence`,
`category`, `source_counts`) so you can feed it to other tooling.

### Use it for onboarding

- **Humans:** run `agentrecall dna --markdown --output PROJECT_DNA.md`, commit it, and
  point new contributors at it. Regenerate it whenever the rule corpus changes.
- **Agents:** paste `agentrecall dna` (or the Markdown) into an agent's context at the
  start of a session so it inherits the project's conventions before writing any code.

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
| `capture_status` | Report the last turn-finalization result (captured/suggested/skipped rule ids, source, timestamp). Call it to answer "did AgentRecall capture anything?" instead of guessing — equivalent to `agentrecall finalize-turn status`. |
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
      { "hooks": [ { "type": "command", "command": "PATH=$HOME/.dotnet/tools:$PATH agentrecall hook user-prompt-submit" } ] }
    ]
  }
}
```

> **PATH note.** Claude Code runs hooks through a non-login shell that may not have
> `~/.dotnet/tools` on `PATH` — so a bare `agentrecall` can fail with
> `command not found`. The command above prepends the .NET global-tools directory so
> the hook resolves regardless of how Claude Code was launched. If you still hit
> "command not found", make sure .NET global tools are on the PATH Claude Code sees:
>
> ```bash
> export PATH="$HOME/.dotnet/tools:$PATH"
> ```
>
> For hooks specifically, add that through a Claude Code `SessionStart` hook (which
> Claude Code applies to every session and to the subprocesses it spawns), or launch
> Claude Code from a shell where the path is already configured. `agentrecall
> devcontainer init` writes the PATH-prefixed form for you, and re-running it upgrades
> an older bare command in place.

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

## Turn Finalizer

Recall is deterministic through the UserPromptSubmit hook — and so is **capture**.
After a turn finishes, AgentRecall finalizes it through a single command:

```
Stop hook
  → agentrecall finalize-turn
  → extract candidate lessons   (user corrections, lessons the agent flagged)
  → classify worthiness         ("lessons, not facts")
  → detect duplicates & conflicts
  → decide AutoCapture / SuggestCapture / Skip   (the same decision policy every flow uses)
  → store / suggest / skip, then report a structured summary
```

This makes AgentRecall — not the agent — the owner of the capture decision. The
agent should **not** guess whether the Stop hook captured something, and should
**not** ask "want me to save it?". When you want to know, ask AgentRecall:

```bash
agentrecall finalize-turn status      # the last finalization result
```

It reuses the existing pipeline end-to-end (the worthiness classifier, the rule
extractor, duplicate detection, the conflict detector, and `FeedbackService`), so
behaviour is identical to every other capture path — there is no parallel logic.

**Use it.** `finalize-turn` reads a Claude Code Stop-hook payload on stdin
(tolerant of missing fields — `cwd`, `prompt`, `assistant_response`,
`transcript`/`transcript_path`, `source`):

```bash
agentrecall finalize-turn < payload.json          # human-readable summary
agentrecall finalize-turn --json < payload.json   # structured result
agentrecall finalize-turn status                  # show the last finalization
agentrecall finalize-turn status --json
agentrecall capture-status --last-turn            # alias for status
```

A captured turn reads:

```
AgentRecall finalized turn.

Captured:
- #14 Repository rule: When emitting validator messages, apply the same tenant scope…

Skipped:
- Duplicate of rule #12.
- Looks like a bare method recommendation, which is recoverable from the repository with search.

Suggested:
- #15 Pending rule: Don't re-query what you already loaded.
```

When nothing reusable was said, it prints `No lessons found.`

**It never blocks Claude Code.** The command always exits 0, logs errors to stderr,
makes no network or LLM calls, and is deterministic. Malformed input mutates nothing.

**Duplicates are avoided.** Capture is deduplicated against existing rules (same
normalized guidance and scope), so:

- if a lesson was already captured this turn (a manual `capture_feedback`, or an
  earlier candidate), the finalizer records a **duplicate skip** instead of a second
  rule;
- re-running the finalizer on the same turn is **idempotent** — it returns the prior
  result and creates nothing new.

**Privacy.** The raw transcript is **not** stored by default — only a content hash,
the resulting rule ids, and skip reasons. Set `StoreTurnTranscript: true` to keep
the transcript for debugging.

**Wiring.** `agentrecall devcontainer init` registers the finalizer as the Stop hook
and upgrades an older `agentrecall hook capture` registration in place:

```json
{
  "hooks": {
    "Stop": [
      { "hooks": [ { "type": "command", "command": "PATH=$HOME/.dotnet/tools:$PATH agentrecall finalize-turn --hook" } ] }
    ]
  }
}
```

> **Stop-hook limitation.** Finalization is only automatic when the Stop hook is
> installed. The hook payload Claude Code provides may not include the full prompt
> and assistant response inline; the finalizer falls back to the referenced
> `transcript_path` when available, and to whatever fields are present otherwise.
> The command always works when run manually, regardless of the hook.

**Configure it** in `agentrecall.json`:

| Setting | Default | Purpose |
| --- | --- | --- |
| `TurnFinalizerEnabled` | `true` | Master switch; `false` makes finalization a no-op. |
| `StoreTurnTranscript` | `false` | Persist the raw transcript with each finalization. |
| `MaxCandidatesPerTurn` | `5` | Maximum lessons captured from one turn. |
| `MaxCandidateCharacters` | `1000` | Per-candidate length cap (bounds a huge turn). |
| `CaptureAutoConfidence` | `0.5` | Minimum confidence to auto-capture without explicit acceptance. |
| `FinalizerShowUserNotice` | `true` | Emit a Stop-hook `systemMessage` notice after a turn. |
| `SuppressDuplicateNotices` | `true` | Stay silent when only a duplicate was reinforced. |

### Asking AgentRecall what it did (don't guess)

AgentRecall records every run and every capture decision, so its state is always
**queryable** — an agent should never answer a question about it by speculating or by
reasoning from whether it personally called a tool. Use the command that matches the
question:

| User asks | Command to run |
| --- | --- |
| Did you save anything? / Was anything captured? / Any lesson for AgentRecall? | `agentrecall capture-status --last-turn` |
| Did AgentRecall run? / What did AgentRecall do? / What rules were fetched? | `agentrecall activity last` |
| Did the Stop hook capture anything? | `agentrecall finalize-turn status` |

The generated `CLAUDE.md` encodes this as a behavior contract (`agentrecall
devcontainer init` writes it, and refreshes an older block in place). The agent must
check status first, report the actual recorded result, and only offer manual capture
when status shows nothing was captured **and** the user explicitly asks to save.

### Troubleshooting: "the Stop hook may have captured it"

If the agent answers a capture question by guessing — *"the Stop hook may have
captured it"*, *"I didn't manually call AgentRecall"*, *"I don't control whether it
fired"*, or *"want me to save it?"* — it is not consulting the recorded decision. Fix
it deterministically:

1. **Ask AgentRecall, don't guess.** The answer is one command away:

   ```bash
   agentrecall finalize-turn status      # or: agentrecall capture-status --last-turn
   ```

   It reports the last finalization — captured rule ids, suggested/pending ids,
   skipped reasons, and duplicates — or `No finalized AgentRecall capture is
   recorded for the last turn.`

2. **Verify the Stop hook is installed.** `.claude/settings.json` should contain a
   `Stop` hook running `…agentrecall finalize-turn --hook`. Re-run `agentrecall
   devcontainer init` to install or upgrade it (it replaces an older `agentrecall
   hook capture` registration in place).

3. **Verify the installed tool supports it.** `agentrecall finalize-turn status`
   must be a known command; if it errors, update the global tool:

   ```bash
   dotnet tool update --global AgentRecall
   agentrecall --version
   ```

4. **Reinforce the guidance.** The scaffolded `CLAUDE.md` block tells the agent to
   check status before answering and never to speculate. If your project pre-dates the
   current block, re-run `agentrecall devcontainer init` — it refreshes an older
   AgentRecall block in place (no duplicate) rather than appending a second copy.

The MCP `capture_status` tool returns the same result for agents that prefer a tool
call over the CLI.

> **In short:** if Claude still says *"the Stop hook may have captured it"*, update
> AgentRecall (`dotnet tool update --global AgentRecall`) and re-run `agentrecall
> devcontainer init` to install the current Stop hook and the guidance that points
> the agent at `capture_status`.

---

## Outcome-aware capture

AgentRecall captures lessons not only from text, but from **evidence that the agent
actually made a mistake or the user corrected behaviour**. The same words can be worth
keeping or worth skipping depending on what produced them.

A generic refactoring rule is normally skipped as textbook advice:

> "Preserve else semantics when flattening nested conditionals."

But if the agent really *did* flatten nested template conditionals and changed the
`{{else}}` behaviour — and the user corrected it — that is no longer generic advice. It
is an observed agent failure, and AgentRecall captures it as a reusable, conditional
lesson:

> When flattening nested template conditionals, preserve `{{else}}` semantics. If the
> inner `if` has an `else`, use an equivalent branch-preserving form such as
> `{{else if (not …)}}` instead of a plain `(and …)` merge.

This is deterministic and layered on top of the existing decision policy — it never
replaces it. The text-only worthiness verdict and the capture decision are computed
first; an **adaptive worthiness policy** then raises or lowers that decision using the
outcome context (no LLM, no embeddings, same inputs → same output).

The rules are deterministic:

- Generic advice with **no observed mistake** → skipped.
- Generic advice backed by an **observed agent failure** or **user correction** →
  captured or suggested (and rewritten into conditional form).
- A bare **code fact is still rejected**, even when something broke — it is recoverable
  from the repository with search and is never auto-captured.
- **Project-specific conventions** are still captured.
- **Repeated** corrections raise confidence and strongly favour capture.
- A **duplicate** reinforces the existing rule; a **conflict** is held for review.
- An explicit **"do not save"** skips; an explicit **"save this"** can capture a worthy
  low-confidence lesson.

### Capture reasons

Every adaptive capture records *why* it was kept, persisted on the rule and surfaced by
`agentrecall rules explain <id>`:

| Reason | What it means |
| --- | --- |
| `ObservedAgentFailure` | The agent's output broke or changed behaviour this turn. |
| `UserCorrection` | The user corrected the agent ("no, preserve the else branch"). |
| `AcceptedReviewComment` | An accepted/applied code-review comment. |
| `RepeatedCorrection` | The same correction was observed two or more times. |
| `LessonMined` | Surfaced by lesson mining over repeated historical signals. |

```
$ agentrecall rules explain 24

Rule:
When flattening nested template conditionals, preserve `{{else}}` semantics …

Captured because:
ObservedAgentFailure

Evidence:
Agent changed `{{else}}` behavior while flattening nested conditionals; user corrected the implementation.
```

The turn finalizer detects these signals from the turn ("that broke behavior", "no,
preserve the else branch", "you changed semantics", "the review comment was applied",
"tests failed because…", "this is the same mistake again") and passes them into the
adaptive policy, so capture stays deterministic and the agent never has to guess.

---

## Interactive Memory

AgentRecall usually captures high-confidence lessons automatically. When it is unsure,
it can **ask** you whether to remember the lesson — turning an ambiguous capture into a
quick, visible choice instead of a silent pending rule you forget about. It reuses the
existing capture decision (`AutoCapture` / `SuggestCapture` / `Skip`); Interactive Memory
only changes how a `SuggestCapture` is surfaced. It never re-classifies worthiness.

- **AutoCapture** → stored automatically, with a notice. No question.
- **SuggestCapture** → AgentRecall asks (when a terminal is attached).
- **Skip** → never asks.

When a terminal is attached, an ambiguous lesson is shown as a prompt:

```
🧠 **AgentRecall:** possible lesson detected.

Candidate:
When flattening nested template conditionals, preserve `{{else}}` semantics.

Why:
This came from an observed agent mistake, but the rule may be broad.

Actions:
[y] Remember
[n] Ignore
[v] View details
```

- `y` approves the pending rule (it becomes Active): `🧠 **AgentRecall:** remembered rule #31.`
- `n` archives it: `🧠 **AgentRecall:** ignored suggestion #31.`
- `v` shows the full rule, reason, confidence, evidence, and scope, then asks again.

### Modes

`AgentRecall.InteractiveMemoryMode` controls whether AgentRecall asks. It is **distinct
from `ActivityNoticeLevel`** — that controls how loud the notices are; this controls
whether AgentRecall prompts.

| Mode | Behaviour |
| --- | --- |
| `Auto` (default) | Capture high-confidence lessons automatically; ask only for ambiguous `SuggestCapture`. |
| `Ask` | More conservative; a borderline auto-capture is downgraded to a question. |
| `Silent` | Never prompts; suggestions become Pending rules to approve later. |

```jsonc
// agentrecall.json
{ "AgentRecall": { "InteractiveMemoryMode": "Auto" } }
```

### Non-interactive surfaces never block

Hooks (`finalize-turn --hook`), pipes, and MCP never wait for input. An ambiguous lesson
becomes a Pending rule and the output names the follow-up command:

```
🧠 **AgentRecall:** suggested 1 pending rule.
Run `agentrecall rules approve 31` to remember it.
```

Over MCP, the structured `capture_feedback` / `add_feedback` response carries
`capture_decision: "SuggestCapture"`, `pending_rule_id`, and
`suggested_actions: ["approve", "reject", "view_details"]` — no terminal prompt text.

Approve or ignore a pending suggestion later with the existing rule commands:

```bash
agentrecall rules list --status Pending   # see what is waiting
agentrecall rules approve <id>            # remember it (becomes Active)
agentrecall rules archive <id>            # ignore it
agentrecall capture-status --last-turn    # what the last turn captured / suggested / skipped
```

---

## Activity Notices

AgentRecall is **visible by default**. As you work, it tells you what it just did —
what it fetched, captured, skipped, resolved, mined, and recommended — with a
recognizable badge:

```
🧠 **AgentRecall:** captured 1 new rule.
- #24 Validator auth/scope safety
```

Crucially, the human-facing notices are kept **separate from the model-visible
context**. Verbose detail goes to your terminal and the activity log; the hook that
injects rules into Claude stays compact, so notices never bloat the token budget.

**Verbosity is configurable** with two independent settings:

```json
{
  "AgentRecall": {
    "ActivityNoticeLevel": "Verbose",
    "HookNoticeLevel": "Normal"
  }
}
```

- **`AgentRecall.ActivityNoticeLevel`** = `Verbose` | `Normal` | `Silent` — controls
  the human-facing CLI/status notices. Defaults to `Verbose`.
  - `Verbose` — summary plus useful detail bullets:

    ```
    🧠 **AgentRecall:** captured 1 new rule.
    - #24 Validator auth/scope safety
    ```
  - `Normal` — a concise summary only:

    ```
    🧠 **AgentRecall:** fetched 3 rules · captured 1 · skipped 1
    ```
  - `Silent` — no user-visible notices (activity is still recorded, and errors still
    go to stderr/logs).
- **`AgentRecall.HookNoticeLevel`** = `Normal` | `Silent` — controls the notice the
  UserPromptSubmit hook injects alongside the rules. Defaults to `Normal`. It is
  always a single compact line and never carries verbose detail or repeats rule
  text, so it cannot inflate the injected context:

  ```
  🧠 **AgentRecall:** fetched 3 rules.
  ```

  An invalid value for either setting falls back to its default and prints a clear
  warning rather than failing.

**Review what AgentRecall has done** with the activity log:

```bash
agentrecall activity last            # the latest notice (verbose)
agentrecall activity list            # recent notices, newest first
agentrecall activity list --json     # structured output for tooling
agentrecall activity list --limit 20 # show more entries
```

`--json` emits plain fields (no Markdown); the styled badge string lives only in a
separate `renderedNotice` field. When feeding `inject-context` output to a model,
pass `--no-notice` to suppress the human notice entirely.

---

## Configuration

AgentRecall reads an optional `agentrecall.json` from the current directory, then
applies environment-variable overrides prefixed with `AGENTRECALL_`.

```json
{
  "AgentRecall": {
    "DataDirectory": "~/.agentrecall",
    "LogLevel": "Information",
    "AutoApproveFeedback": true,
    "ActivityNoticeLevel": "Verbose",
    "HookNoticeLevel": "Normal"
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
