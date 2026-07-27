# AgentRecall

[![CI](https://github.com/AkbarDizaji/AgentRecall/actions/workflows/ci.yml/badge.svg)](https://github.com/AkbarDizaji/AgentRecall/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/AkbarDizaji/AgentRecall/branch/main/graph/badge.svg)](https://codecov.io/gh/AkbarDizaji/AgentRecall)
[![Indexed on TensorBlock MCP Index](https://mcp-index.tensorblock.co/v1/servers/github-akbardizaji-agentrecall-9b7529bd/badge.svg)](https://tensorblock.co/mcp/servers/github-akbardizaji-agentrecall-9b7529bd)

**A local-first memory for AI coding agents.** AgentRecall turns the feedback and
failures you run into while coding into reusable rules, then serves the right
ones back — on the command line or directly to Claude Code over MCP. Everything
stays on your machine: no cloud sync, no web UI, no API keys.

## Contents

- [What it does](#what-it-does)
- [Install](#install)
- [Quick start](#quick-start)
- [Using AgentRecall](#using-agentrecall)
- [Command reference](#command-reference)
- [User Preferences](#user-preferences)
- [Standing Rules](#standing-rules)
- [Project DNA](#project-dna)
- [Seed Packs](#seed-packs)
- [Career Impact Pack](#career-impact-pack)
- [Capture pipeline](#capture-pipeline)
- [Interactive Memory](#interactive-memory)
- [Activity Notices](#activity-notices)
- [Turn Memory Summary](#turn-memory-summary)
- [Claude Code integration](#claude-code-integration)
- [Configuration](#configuration)
- [Development](#development)

## What it does

- **Turns feedback into structured rules.** Each correction is parsed into a
  `trigger`, `rule`, `do`, `do_not`, `reason`, and `applies_to`, and validated —
  not stored as a raw note.
- **Ranks what to surface.** `search` scores rules by keyword + a deterministic
  concept graph (e.g. *refund* relates to *money* — not vector embeddings)
  + scope + confidence, then buckets results into **must-follow**,
  **suggested**, and **warnings** within a token budget. An `IEmbeddingProvider`
  extension point exists for hybrid search, but ships as a no-op — ranking is
  keyword + concept based and fully local until you configure one.
- **Applies universal constraints every turn.** Style/tone/quality rules that
  belong on *every* task are captured as [standing rules](#standing-rules) and
  injected regardless of keyword relevance.
- **Resolves conflicts automatically.** When rules disagree, the policy engine
  picks the effective one by scope, explicit supersede, priority, recency, then
  confidence.
- **Learns from failures.** Build/test/lint logs feed back in; repeated
  failures raise a rule's confidence and can auto-promote the fix.
- **Compresses its own memory.** Duplicate and overlapping rules are detected
  and merged, with originals kept as an audit trail.
- **Measures retrieval quality.** A bundled eval reports Precision@1/@3 and
  Recall@5, and fails CI on a regression.
- **Injects deterministically.** A gated Claude Code hook prepends relevant
  rules to context on every matching prompt — not just when the model
  remembers to ask.

## Install

AgentRecall is a .NET global tool. Install the [.NET SDK](https://dotnet.microsoft.com/download)
(8+), then:

```bash
dotnet tool install -g AgentRecall
agentrecall --version
```

**PATH.** A fresh install often isn't on `PATH` in your current shell, so
`agentrecall` can report "command not found" right after installing — the tool
fixes its own PATH on first run and prints a one-time notice, so a new terminal
picks it up automatically. If the very first invocation is the one that fails,
either open a new terminal or use the bootstrap script below, which installs
**and** fixes PATH in one step:

```bash
# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/AkbarDizaji/AgentRecall/main/scripts/install.sh | bash
```

```powershell
# Windows (PowerShell)
iwr -useb https://raw.githubusercontent.com/AkbarDizaji/AgentRecall/main/scripts/install.ps1 | iex
```

You can also fix PATH any time with `agentrecall setup`. Update/remove later
with `dotnet tool update -g AgentRecall` / `dotnet tool uninstall -g AgentRecall`.

### Dev containers

A global tool lives under `~/.dotnet/tools`, which is wiped on container
rebuild. Run once per project to make the install and hook wiring survive
rebuilds:

```bash
agentrecall devcontainer init
```

This wires the `UserPromptSubmit` hook (see [Claude Code integration](#claude-code-integration))
and appends the `CLAUDE.md` guidance block — merged in, never overwritten. **Re-run this after
every AgentRecall upgrade**: if the guidance block has drifted from what the new version ships
(e.g. a newer capture-judge or document-opportunity contract), the re-run refreshes it in place,
so existing projects always get the latest instructions without hand-editing `CLAUDE.md`; when
it's already current, the re-run is a true no-op. Dev-container scaffolding itself is separate and
**opt-in**: if a `devcontainer.json` already exists it's wired in automatically;
if not, none is created — pass `--create` to generate one that reinstalls
AgentRecall, persists data on a named volume, and re-registers the MCP server
on every rebuild.

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

That's the whole loop: **capture → recall.**

## Using AgentRecall

**Capture feedback** — `--task` and `--feedback` are required; the rest add
context that improves matching:

```bash
agentrecall feedback add \
  --task "writing a SQL query for user lookup" \
  --feedback "use parameterized queries to avoid injection" \
  --bad-output "string-concatenated SQL" --fixed-output "a parameterized command with @name" \
  --scope-level Repository --scope-value my-repo --tags "security,sql"
```

**Search and read rules**, ranked by relevance, status, and confidence:

```bash
agentrecall search "sql injection"
agentrecall rules list
agentrecall rules show 1
```

**Curate.** Captured rules are **Active** by default. Lifecycle states are
`Draft → Pending → Active → Promoted`, plus dead-end states `Superseded`,
`Retired`, `Archived` (excluded from search and dedup):

```bash
agentrecall rules promote 1     # Active   → Promoted
agentrecall rules approve 1     # Pending  → Active
agentrecall rules supersede 1 2 # replace rule 1 with rule 2
agentrecall rules archive 3     # hide from search
```

Prefer to review before a rule counts? Capture with `--pending`, or set
`AutoApproveFeedback: false` to make it the default.

**Learn from failures.** Failures that match an existing rule raise its
confidence and can auto-promote the fix:

```bash
agentrecall import build-log ./build.log
agentrecall import test-log ./test.log
agentrecall import lint-log ./lint.log
```

**Learn from PR review comments.** Each comment that reads as a reusable
correction becomes a **pending** rule (tagged `pr-review`); praise/questions/nits
are skipped. Add `--accepted` if you've already acted on the comments, so they
land as **Active** rules directly:

```bash
gh pr view 42 --json comments > comments.json
agentrecall import pr-comments ./comments.json --task "PR #42: add refunds" --scope-value my-repo
```

**Evaluate retrieval quality** against a bundled dataset (a throwaway store —
your real database is never touched). Exits non-zero on a regression below
baseline; the same check runs as a unit test:

```bash
agentrecall eval retrieval
agentrecall eval retrieval --dataset ./my-eval.json
```

## Command reference

| Command | Description |
| --- | --- |
| `init` | Create the local data directory and database. |
| `setup` | Ensure the .NET tools directory is on your PATH (runs automatically on first use). |
| `devcontainer init` | Wire AgentRecall's hooks + CLAUDE.md guidance; `--create` also scaffolds a dev container (optional `[path]`). |
| `feedback add` | Record feedback and extract a rule from it. |
| `search "<query>"` | Search rules by keyword (`--scope-level`, `--scope-value`, `--limit`). |
| `rules list` | List all rules (`--status <status>`). |
| `rules show <id>` | Show a single rule in detail. |
| `rules approve <id>` | Move a Pending rule to Active. |
| `rules promote <id>` | Promote a rule. |
| `rules supersede <oldId> <newId>` | Replace one rule with another (versioned). |
| `rules archive <id>` | Archive a rule (excluded from search). |
| `import build-log / test-log / lint-log <file>` | Ingest failures. |
| `import pr-comments <file>` | Capture PR review comments as rules (`--task`, `--scope-level`, `--scope-value`, `--tags`, `--accepted`). |
| `inject-context "<task>"` | Build agent-ready context (must-follow, warnings, preferred/anti-patterns). |
| `dna` | Summarise the repo's engineering personality (`--markdown`, `--json`, `--top <n>`, `--scope-level`, `--scope-value`, `--output <file>`). |
| `eval retrieval` | Evaluate retrieval quality (`--dataset <path>`); non-zero exit below baseline. |
| `activity last` / `activity list` | Show recent activity notices (`--json`, `--limit <n>`). |
| `turn-summary --last` | Aggregated Turn Memory Summary for the last turn (`--json`, `--detailed`, `--compact`). |
| `seed list` / `show <pack>` / `install <pack>` / `remove <pack>` / `status` | Manage seed packs (`--active`, `--suggested`, `--force`, `--json`). |
| `career impact --last` / `career journal --last` / `career status` | Career-impact pack commands (`--json`, `--detailed`, `--file <path>`). |
| `cleanup pending-noise` | Archive noisy Pending rules from before source/outcome-aware capture (dry run by default; `--apply`, `--json`, `--tag`, `--status`). |
| `finalize-turn` / `finalize-turn status` | Run or inspect turn finalization (`--json`, `--hook`). |
| `capture-status --last-turn` | Alias for `finalize-turn status`. |
| `mcp` | Run the MCP server over stdio (for Claude Code). |
| `status` | Show where data is stored. |
| `help`, `version` | Usage / installed version. |

Run `agentrecall help` any time for the same list.

## User Preferences

Explicit, durable statements about how *you* want the assistant to behave
(*"answer short and simple"*, *"don't ask me too many questions"*) are captured
as a `UserPreference` or `CommunicationPreference` — separate from repository
conventions and engineering lessons:

- **High confidence, user scope.** Captured at ≈0.9 confidence and stored
  globally (not per-repository), since it applies to you everywhere.
- **Normalized, not verbatim.** *"always answer caveman form"* becomes durable,
  bounded guidance; overbroad absolutes are dropped, intent is kept.
- **Unsafe preferences are refused** — one that conflicts with correctness or
  honesty (*"always agree even if I'm wrong"*) is never stored.
- **Response language is never decided by AgentRecall.** A stated language
  preference is captured verbatim as a general preference; it does not choose a
  default language for the model.

```bash
agentrecall feedback add --task "how to answer me" \
  --feedback "From now on, answer short and simple, add examples only if needed."
agentrecall rules explain <id>   # Type: CommunicationPreference, evidence, scope, confidence
```

## Standing Rules

Most rules are **contextual** — retrieved by relevance to the task at hand.
That structurally misses **universal constraints** (*"don't leave unnecessary
comments"*, *"always run the formatter"*): they share no keywords with most
tasks, so they'd never clear the relevance floor. A rule carrying the
`AlwaysApply` flag is delivered on **every** task instead, as must-follow
guidance (or a warning, if it's a prohibition).

`AlwaysApply` (delivery: standing vs. contextual) and `ScopeLevel` (where a rule
is true: `Global`/`Language`/`Repository`/path) are orthogonal — a standing
rule is usually `Global`, but the two are independent properties.

A rule becomes standing when: it's a preference (always standing), the capture
judge flags it as a universal style/process/quality constraint, or you keep
making the same correction (the repeated-correction backstop promotes it).
Standing rules show as `[standing]` in the Turn Memory Summary.

The standing band is **capped at 5 highest-confidence rules per prompt** so it
stays prominent instead of becoming wallpaper; anything beyond the cap falls
back to ordinary relevance ranking rather than being dropped.

Good standing rules are universal and actionable (*"Run the formatter before
finishing a change"*). Bad ones are contextual guidance in disguise (*"Use
`Money` value objects in the billing service"* — scope it to billing instead)
or vague absolutes (*"Never use inheritance"* → prefer *"favor composition over
inheritance"*, kept contextual).

## Project DNA

A single, onboarding-ready summary of a repository's *engineering
personality* — conventions, patterns, risks, and recurring lessons — meant to
get a new developer or agent productive in under five minutes. It reads the
same data as the rest of AgentRecall (active/promoted rules, retrieval
frequency, mined lessons, conflicts) and organizes it into fixed sections: Core
Principles, Repository Conventions, Testing Patterns, Architecture Patterns,
Error Handling, Feature Gates/Authorization/Security, Common Mistakes, Agent
Warnings, Stale or Risky Knowledge.

Unlike `search` (which rule matches *this* task?) or `report` (metrics), DNA is
a **curated, deterministic narrative** of the whole corpus — no LLM calls, no
embeddings, same inputs always produce the same output.

```bash
agentrecall dna                                    # human-readable
agentrecall dna --markdown --output PROJECT_DNA.md # for onboarding docs
agentrecall dna --json                              # stable, snake_case JSON
```

Commit the Markdown output as `PROJECT_DNA.md`/`CONTRIBUTING.md`, or paste
`agentrecall dna` into an agent's context at the start of a session.

## Seed Packs

Optional **starter engineering memories** — curated rules that give an agent
useful instincts before a project has accumulated its own. They're opt-in per
pack, never installed automatically, and removable at any time.

```bash
agentrecall seed list                    # available packs, installed or not
agentrecall seed install tidy-first      # install as Active (default)
agentrecall seed install tidy-first --suggested  # install as Pending instead
agentrecall seed remove tidy-first       # archive the pack's rules
```

- **Active by default**, at moderate confidence (~0.65); marked with source
  `BuiltInSeed` and a `[seed]` marker in the Turn Memory Summary.
- **Ranked below learned rules** — your own repository conventions and
  corrections always outrank and win conflicts against seed rules, and a seed
  rule is never injected as must-follow. Capped at two per prompt unless the
  task is itself about tidying/refactoring.
- **Not absolute** — reject or archive a seed rule that doesn't fit; that
  lowers its confidence like any learned rule.
- **Idempotent** — installing only adds rules, never edits/deletes yours;
  reinstalling never duplicates or resurrects a removed rule without `--force`.

The built-in `tidy-first` pack has ten rules on separating behavior-preserving
cleanup from behavior change (guard clauses, naming, extraction, scoping a
tidy). It's original, paraphrased guidance — no copied book, article, or
third-party text.

## Career Impact Pack

The `career-impact` seed pack helps you notice and document promotion-worthy
engineering work — impact, evidence, metrics, stakeholders, ADRs — as coaching
guidance, not project facts. It installs like any other seed pack:

```bash
agentrecall seed install career-impact
agentrecall career impact --last       # last turn's candidate
agentrecall career journal --last      # promotion-ready entry, on demand
agentrecall career status              # pack/mode and the last candidate
```

It's **low-token by design**: a cheap, deterministic end-of-turn detector (no
LLM, no embeddings, no network) decides whether a turn was significant; full
impact/journal detail generates only on demand or when a significant candidate
already exists.

- **`SignificantOnly` by default.** `AgentRecall.CareerImpactMode` can also be
  `Silent` (never auto-run) or `Always` (surface lower-confidence candidates).
- **Never spams.** The automatic summary is at most five bullets plus a
  pointer, and is human-visible only — never fed back into model context.
- **Retrieval is capped**, like any seed pack — ranked below learned rules.

It's original, paraphrased career-impact coaching guidance — no copied book,
article, or third-party text.

## Capture pipeline

Recall is deterministic through the `UserPromptSubmit` hook. **Capture** — what
gets remembered — runs through a separate pipeline each time a turn finishes:

```
Stop hook → finalize-turn → semantic capture judge verdict → validate
  → map by confidence → store / suggest / skip / reinforce / supersede
  → structured summary
```

**The semantic capture judge decides, not keyword heuristics.** A word like
"validation" or "auth" appearing in a sentence is never enough on its own to
capture anything.

- **Explicit save/do-not-save requests are always respected**, even for narrow
  or stylistic rules.
- **Confidence maps to outcome:** `≥0.80` captures, `0.55–0.79` suggests a
  Pending rule, `<0.55` skips.
- **Duplicates are reinforced, not duplicated**; an explicit supersede replaces
  the prior rule.
- **If the judge is unavailable, automatic capture is skipped** for that turn —
  there is no keyword fallback. The verdict is supplied by the host as a
  `judgment` object on the `finalize-turn` payload (AgentRecall makes no
  network/LLM calls itself); Claude Code's native Stop hook alone has no such
  field, so `agentrecall devcontainer init` scaffolds `CLAUDE.md` guidance
  telling the model to produce the verdict itself before the native hook fires.
- **Source and outcome matter as much as wording.** Documentation, tool
  instructions, command output, and logs are not captured merely because they
  were read — only when paired with an observed agent failure, a user
  correction, an explicit save, or a confirmed repository convention. A bare
  code fact is never auto-captured (it's recoverable from the repo via search).
  Repeated corrections raise confidence and favor capture.
- **Privacy.** The raw transcript is not stored by default — only a content
  hash, resulting rule ids, and skip reasons (`StoreTurnTranscript: true` to
  keep it for debugging).

**Ask AgentRecall, don't guess.** The agent should never speculate about
whether something was captured — it's one command away:

```bash
agentrecall finalize-turn status      # or: agentrecall capture-status --last-turn
agentrecall activity last             # what AgentRecall did last, in general
```

`agentrecall devcontainer init` scaffolds a `CLAUDE.md` contract requiring the
agent to check status first and only offer manual capture when nothing was
captured **and** you explicitly ask. If an agent still says "the Stop hook may
have captured it", update the tool (`dotnet tool update --global AgentRecall`)
and re-run `agentrecall devcontainer init` to refresh the Stop hook and
guidance.

**Cleaning up old noise.** For Pending rules created before source/outcome-aware
capture existed, `cleanup pending-noise` finds and archives them (dry run by
default, never touches Active/Promoted/user-edited rules):

```bash
agentrecall cleanup pending-noise            # dry run
agentrecall cleanup pending-noise --apply    # archive the noisy ones
```

## Interactive Memory

When the capture judge is unsure (`SuggestCapture`), AgentRecall can **ask**
instead of silently parking a Pending rule — but only when a terminal is
attached; hooks, pipes, and MCP never block waiting for input.

```
🧠 AgentRecall: possible lesson detected.
[y] Remember   [n] Ignore   [v] View details
```

`AgentRecall.InteractiveMemoryMode` controls this: `Auto` (default — ask only
for ambiguous suggestions), `Ask` (more conservative), `Silent` (never prompt;
everything ambiguous becomes Pending). Approve or ignore later the normal way:

```bash
agentrecall rules list --status Pending
agentrecall rules approve <id>   # remember
agentrecall rules archive <id>   # ignore
```

## Activity Notices

AgentRecall tells you what it's doing — what it fetched, captured, skipped,
resolved — with a recognizable badge, e.g. `🧠 AgentRecall: captured 1 new
rule.` This is kept **human-facing only**, separate from the model-visible
context, so notices never bloat the token budget.

Verbosity is independently configurable: `ActivityNoticeLevel`
(`Verbose`/`Normal`/`Silent`, default `Verbose`) for CLI/status notices, and
`HookNoticeLevel` (`Normal`/`Silent`, default `Normal`) for the single line the
`UserPromptSubmit` hook injects alongside rules.

```bash
agentrecall activity last            # the latest notice
agentrecall activity list            # recent notices, newest first
agentrecall activity list --json --limit 20
```

## Turn Memory Summary

At the end of each turn, AgentRecall prints **one aggregated summary** of
everything it did — rules used, captured, suggested, skipped, remembered/
ignored, errors — instead of many scattered notices. It's human-visible only
(never re-injected into model context) and bounded (short titles, capped item
counts).

`AgentRecall.TurnSummaryLevel` = `Silent` | `Compact` (default) | `Detailed`.

```bash
agentrecall turn-summary --last             # at the configured level
agentrecall turn-summary --last --detailed  # force grouped detail
agentrecall turn-summary --last --json
```

## Claude Code integration

Register the MCP server once:

```bash
claude mcp add agentrecall agentrecall mcp
```

| Tool | What it does |
| --- | --- |
| `inject_context` | Given a task, return ranked/conflict-resolved rules bucketed into must-follow, suggested, warnings. |
| `resolve_rules` | Decide which of several matching rules to follow, resolving conflicts/supersessions. |
| `compress_memory` | Merge duplicate/overlapping rules (dry run by default). |
| `get_relevant_context` | Keyword-based rules to know before starting a task. |
| `get_project_rules` | Rules that always apply here (project → promoted → active). |
| `get_reminders` | A short checklist for a kind of work. |
| `capture_status` | The last turn-finalization result — the answer to "did AgentRecall capture anything?". |
| `search_rules` | Find rules relevant to a query. |
| `suggest_feedback_candidate` | Detect whether a message is a reusable correction. |
| `capture_feedback` | Save a correction in one step (`pending=true` to require approval). |
| `add_feedback` | Record feedback with full task/scope context. |
| `import_pr_comments` | Capture PR review comments as pending rules. |

Each rule returns as `trigger`, `rule`, `do`, `do_not`, `reason`, `applies_to`,
`confidence`, `status`. The server speaks JSON-RPC 2.0 over stdio, logs only to
stderr, and behaves identically installed or run from source.

**Registering the server only makes tools available** — the agent still has to
call them. Add this to your `CLAUDE.md`/`AGENTS.md` to make it default
behavior (or let `agentrecall devcontainer init` scaffold it for you):

```markdown
## Memory (AgentRecall)

- **Before** coding/refactoring/reviewing/debugging, call `inject_context` with
  the task. Follow must-follow rules and heed warnings.
- **When the user corrects you**, call `suggest_feedback_candidate`, then
  `capture_feedback` for reusable lessons.
- **After a PR review**, pass comments to `import_pr_comments`.
```

**For deterministic injection**, wire a Claude Code hook instead of relying on
the model to choose to call a tool — hooks run before the model responds,
every time the gate matches:

```json
{
  "hooks": {
    "UserPromptSubmit": [
      { "hooks": [ { "type": "command", "command": "PATH=$HOME/.dotnet/tools:$PATH agentrecall hook user-prompt-submit" } ] }
    ],
    "Stop": [
      { "hooks": [ { "type": "command", "command": "PATH=$HOME/.dotnet/tools:$PATH agentrecall finalize-turn --hook" } ] }
    ]
  }
}
```

On each prompt this gates on `HookKeywords` (dev-shaped prompts only, no cost
otherwise), retrieves rules scoped to the repository, and prepends a compact
`## AgentRecall Technical Context` block (Must Follow / Warnings / Preferred
Patterns / Anti Patterns / Source Rules — empty sections omitted). It never
blocks Claude Code: on error it logs to stderr and injects nothing.

Troubleshoot by running the hook by hand:

```bash
echo '{"prompt":"Write Moq tests for OrderService","cwd":"'"$PWD"'"}' \
  | agentrecall hook user-prompt-submit
```

An empty result means the gate didn't match, the hook is disabled, or nothing
was relevant. `claude --debug` shows hook execution inside Claude Code.

## Configuration

AgentRecall reads an optional `agentrecall.json` from the current directory,
then environment overrides prefixed `AGENTRECALL_` (e.g.
`AGENTRECALL_AgentRecall__LogLevel=Debug`). Data lives in one SQLite database
under `DataDirectory` — backing up or moving your memory is copying that
folder.

```json
{
  "AgentRecall": {
    "DataDirectory": "~/.agentrecall",
    "LogLevel": "Information",
    "AutoApproveFeedback": true,
    "ActivityNoticeLevel": "Verbose",
    "HookNoticeLevel": "Normal",
    "TurnSummaryLevel": "Compact"
  }
}
```

| Setting | Default | Purpose |
| --- | --- | --- |
| `DataDirectory` | `~/.agentrecall` | Where the SQLite database lives. |
| `LogLevel` | `Information` | Log verbosity. |
| `AutoApproveFeedback` | `true` | `false` makes every capture start `Pending`. |
| `ActivityNoticeLevel` | `Verbose` | CLI activity notice verbosity: `Verbose`/`Normal`/`Silent`. |
| `HookNoticeLevel` | `Normal` | Verbosity of the single line the prompt hook injects: `Normal`/`Silent`. |
| `TurnSummaryLevel` | `Compact` | End-of-turn summary verbosity: `Silent`/`Compact`/`Detailed`. |
| `HookEnabled` | `true` | Master switch for the `UserPromptSubmit` hook. |
| `HookKeywords` | dev-task words | Words/phrases that mark a prompt as dev work. |
| `HookMaxRules` | `5` | Max rules injected per prompt. |
| `HookIncludePending` | `true` | Whether Pending rules may be injected, so a repeat can be recognized and reinforced. |
| `HookPendingCap` | `1` | Max Pending rules injected per prompt when `HookIncludePending` is true. |
| `TurnFinalizerEnabled` | `true` | Master switch for `finalize-turn`. |
| `CaptureJudgeMode` | `Semantic` | `Off` disables automatic Stop-hook capture entirely. |
| `StoreTurnTranscript` | `false` | Persist the raw transcript with each finalization. |
| `MaxCandidatesPerTurn` | `5` | Max lessons captured from one turn. |
| `MaxCandidateCharacters` | `1000` | Per-candidate length cap. |
| `CaptureAutoConfidence` | `0.5` | Minimum confidence to auto-capture without explicit acceptance. |
| `FinalizerShowUserNotice` | `true` | Emit a Stop-hook notice after a turn. |
| `SuppressDuplicateNotices` | `true` | Stay silent when only a duplicate was reinforced. |
| `InteractiveMemoryMode` | `Auto` | `Ask`/`Silent` change whether ambiguous captures prompt you. |
| `CareerImpactMode` | `SignificantOnly` | `Silent`/`Always` for the career-impact pack's auto-summary. |
| `CareerImpactSummaryLevel` | `Compact` | `Detailed` for the career-impact auto-summary. |

## Development

```bash
git clone https://github.com/AkbarDizaji/AgentRecall.git
cd AgentRecall
dotnet build && dotnet test
dotnet run --project src/AgentRecall.Cli -- init   # run without installing
```

See [`ARCHITECTURE.md`](ARCHITECTURE.md) for solution layout and design, and
[`CONTRIBUTING.md`](CONTRIBUTING.md) for the contribution workflow, coding
conventions, and how to cut a release.
