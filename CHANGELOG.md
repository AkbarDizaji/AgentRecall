# Changelog

All notable changes to AgentRecall are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.6.0] - 2026-06-24

### Added
- Added automatic rule lifecycle recommendations.
- Added `agentrecall lifecycle suggest|list|show|apply|reject`.
- Added promote, archive, supersede, review, raise-confidence, and lower-confidence recommendations.
- Added lifecycle recommendation reports.

### Safety
- Lifecycle suggestions are dry-run by default.
- Rules are only changed when recommendations are explicitly applied.

## [0.2.9] - 2026-06-18

### Added
- **Deterministic capture, to match deterministic recall.** Recall already runs
  automatically via the `UserPromptSubmit` hook; capture previously depended on the
  model choosing to call an MCP tool, so lessons were frequently lost. A new
  `agentrecall hook capture` command runs as a Claude Code `Stop` hook (after the
  assistant finishes a turn): it reads the turn from the hook payload or transcript,
  and when the user's message is a genuine correction it stores the lesson through
  the same memory-worthiness policy as every other path — code facts are rejected,
  specific facts are generalized, and accepted guidance is stored Active. The hook
  never throws and never blocks (always exits 0), and surfaces a one-line
  `systemMessage` only when a capture decision was actually made.
  `agentrecall devcontainer init` now wires both hooks (recall + capture),
  merge-safe and idempotent.

  Claude Code exposes no hook that delivers the prompt/response inline after a turn;
  the `Stop` hook is the post-response trigger, and it provides a `transcript_path`
  the capture hook parses — so capture is deterministic without any LLM call.

## [0.2.8] - 2026-06-18

### Fixed
- **The tool now installs on machines without the .NET 10 SDK.** AgentRecall was
  packed for `net10.0` only, so `dotnet tool install -g AgentRecall` on a .NET 8 or
  9 SDK failed with the misleading "The settings file in the tool's NuGet package
  is invalid: Settings file 'DotnetToolSettings.xml' was not found in the package."
  — NuGet could not find a compatible `tools/<tfw>` asset. The tool now
  multi-targets `net8.0` (LTS) and `net10.0`, so older SDKs install and run the
  `net8.0` build while .NET 10 keeps using `net10.0`.

### Added
- **"Lessons, not facts" memory-quality policy.** A deterministic
  `MemoryWorthinessClassifier` screens captured feedback before a rule is created:
  low-value code facts (a method/property exists, a file path, one service calling
  another, a bare "use method X") are rejected, reusable lessons are stored, and a
  code fact that hints at a reusable pattern is stored as a generalized lesson. It
  runs across every capture flow (`feedback add`, `capture_feedback`, `add_feedback`,
  PR-comment import) and is configurable via `MemoryWorthinessEnabled`,
  `StoreRejectedCandidates`, and `AllowCodeFactsWhenAccepted`.

## [0.2.7] - 2026-06-18

### Added
- **AgentRecall now puts itself on PATH automatically.** A .NET global tool has no
  post-install hook, so a fresh `dotnet tool install -g AgentRecall` is frequently
  "not found" until the tools directory is on PATH (a common first-run failure,
  especially on Windows PowerShell). The first time the tool runs it adds its
  directory to the persisted user PATH — the shell profile on macOS/Linux, the
  user `PATH` on Windows (preserving `REG_EXPAND_SZ` so entries like `%JAVA_HOME%`
  keep working) — and prints a one-time notice. The machine-facing `mcp` and `hook`
  commands skip this to keep their stdio clean.
- **`agentrecall setup`** performs the same PATH fix on demand, and bootstrap
  install scripts (`scripts/install.sh`, `scripts/install.ps1`) install and
  configure PATH in one step.

## [0.2.6] - 2026-06-17

### Added
- **`agentrecall devcontainer init` now makes recall and capture automatic, not
  just available.** Alongside the MCP registration it wires the deterministic
  `UserPromptSubmit` hook into `.claude/settings.json` (so relevant rules are
  injected on every prompt) and appends a `CLAUDE.md` guidance block. Existing
  settings and `CLAUDE.md` content are merged or left untouched — never
  overwritten — and re-running is a no-op.
- **`import_pr_comments` accepts an `accepted` flag** (CLI `--accepted`, MCP
  `accepted: true`). Accepted comments — ones the user acted on — are recorded as
  **Active** rules instead of Pending. The scaffolded `CLAUDE.md` tells the agent
  to use it: when the user asks it to apply what a review comment says, that
  comment is treated as accepted and captured as an Active rule automatically.

## [0.2.5] - 2026-06-17

### Fixed
- **A globally-installed `agentrecall` is no longer "not found" in dev container
  terminals.** The tool lives in `~/.dotnet/tools`, and VS Code often opens
  terminals as non-login shells that don't read `~/.profile`, so the install
  succeeded but the command wasn't on PATH. The setup script now detects a missing
  `~/.dotnet/tools`, appends it to `~/.bashrc`, and prints the exact `remoteEnv`
  snippet to set it permanently.

### Changed
- **Dev container setup scripts are easier to debug.** Both the generated
  `agentrecall-post-create.sh` and this repo's own `post-create.sh` now log each
  step (`restore`, `pack`, `tool update`, …) and install a failure trap that names
  the command that aborted and states plainly that AgentRecall was not installed.

## [0.2.4] - 2026-06-17

### Fixed
- **The dev container setup script no longer requires `sudo`.** The script
  generated by `agentrecall devcontainer init` called `sudo` unconditionally to
  fix the persisted volume's ownership, so on a minimal image without `sudo` the
  step failed and could abort the whole `postCreateCommand` chain. It now fixes
  ownership using whatever the image offers (already-root, then `sudo` only when
  present), resolves the data directory from the configured env var, and warns
  instead of aborting when the volume can't be made writable.

## [0.2.3] - 2026-06-17

### Added
- **`agentrecall devcontainer init`** scaffolds the dev container wiring so the
  tool reinstalls automatically on every "Rebuild Container". A global .NET tool
  lives on the container filesystem and is wiped by a rebuild; the generated
  `.devcontainer/agentrecall-post-create.sh` reinstalls AgentRecall from NuGet,
  persists the database on a named Docker volume, and re-registers the MCP server
  on each create. When the project has no `devcontainer.json` a complete one is
  generated; an existing manifest is left untouched and the keys to merge are
  printed.

## [0.2.2] - 2026-06-16

### Fixed
- **Search no longer returns unrelated rules.** Relevance matched query terms
  as substrings, so a term like `in` matched inside words such as `domain` or
  `instead`, and `console.writeline` was indexed as a single token. Matching is
  now whole-word, the tokenizer splits on punctuation, and common stop words are
  dropped from queries so matches must land on content words.
- **Capturing feedback no longer creates duplicate rules.** Identical guidance
  for the same scope is now recorded against the existing rule instead of
  inserting a copy. This applies to every caller (CLI and MCP); the MCP feedback
  tools and the CLI report whether an existing rule was reused.

## [0.2.1] - 2026-06-16

### Fixed
- Databases created by an earlier version are now reconciled to the current
  schema on startup. `EnsureCreated` only builds the schema for a brand-new
  file and never updates an existing one, so upgrading could fail with errors
  like `table Rules has no column named LastUsedAt`. A new schema reconciler
  reads the expected tables, columns, and indexes from the EF model and
  additively adds whatever is missing, preserving existing data.

## [0.2.0] - 2026-06-15

### Added
- **Policy engine** (`resolve_rules`): when several rules match a task, decides
  which are effective and which to ignore, resolving direct conflicts (e.g. "use
  the repository pattern" vs "do not") by scope, explicit supersede, priority,
  recency, then confidence.
- **Automatic memory compression** (`compress_memory`): detects duplicate,
  near-duplicate, and overlapping rules and merges each group into one canonical
  rule, preserving the originals and their feedback as an audit trail.
- **Smart context injection** (`inject_context` MCP tool and `inject-context`
  CLI): ranks rules by usefulness (keyword + semantic + domain + task-type +
  scope, weighted by confidence) and returns must-follow rules, warnings,
  preferred patterns, anti-patterns, and source rule ids within a token budget.
- **PR review-comment ingestion** (`import_pr_comments` MCP tool and
  `import pr-comments` CLI): turns actionable reviewer comments into rules and
  skips praise/questions/nits.
- **Retrieval quality evaluation** (`eval retrieval`): a bundled dataset of
  rules and query scenarios reports Precision@1, Precision@3, and Recall@5, and
  fails CI when retrieval drops below baseline.
- **Gated UserPromptSubmit hook** (`hook user-prompt-submit`): deterministically
  injects relevant rule context for development prompts in Claude Code, with a
  configurable keyword gate and graceful failure handling.
- **Structured rule extraction** with a quality validator: derives a readable
  trigger, rule, do, do_not, reason, applies_to, and tags from feedback.
- New `RecallRule` fields: `Priority`, `Deprecated`, `SupersedesRuleId`,
  `LastUsedAt`.
- Configuration options: `AutoApproveFeedback`, `HookEnabled`, `HookKeywords`,
  `HookMaxRules`, `HookIncludePending`.

### Changed
- Captured feedback now produces an **Active** rule by default (was Pending);
  set `AutoApproveFeedback` to `false` to keep the review-first behaviour, or
  pass `--pending` / `pending=true` per call. Bulk PR imports stay Pending.
- Rule extraction no longer prefixes rule text with "When {task}:" (the task is
  kept as the trigger), and the **reason** is no longer derived from the scope
  value.

### Fixed
- `do` and `do_not` are no longer populated with the same sentence; `do_not` is
  left empty when no distinct, prohibitive guidance can be inferred.
- Sentence parsing no longer shreds code containing dots (e.g. `It.IsAny<T>()`).

## [0.1.0]

### Added
- Initial release: local-first capture of feedback into versioned rules, ranked
  keyword search, rule lifecycle (approve/promote/supersede/archive), failure-log
  import, and an MCP server for Claude Code.

[0.2.0]: https://github.com/AkbarDizaji/AgentRecall/releases/tag/v0.2.0
[0.1.0]: https://github.com/AkbarDizaji/AgentRecall/releases/tag/v0.1.0
