# Changelog

All notable changes to AgentRecall are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.8.0] - 2026-08-13

### Added
- **The Stop hook now enforces that a turn is judged instead of silently skipping.** A
  substantive turn that reaches the Stop hook with no semantic capture judgment is blocked
  once: `finalize-turn --hook` emits Claude Code's `{"decision": "block", "reason": …}`
  response asking the session model — the judge — to submit its verdict, the turn resumes
  and calls the new tool, and the next Stop finalizes from the recorded verdict. Previously
  every such turn was recorded as "judge unavailable" and captured nothing, which on a host
  where the model never self-reported meant automatic capture never ran at all.
- **`submit_capture_judgment` MCP tool.** Submits a turn's verdict mid-turn. Its arguments
  are the same `judgment` object the `finalize-turn` payload carries, read by the same
  parser, so the two routes cannot drift; a `Skip` verdict is a first-class answer that
  resolves the request. It also works unprompted (pass `prompt`/`assistant_response`) as an
  alternative to piping a payload into `finalize-turn`.
- **`TurnJudgmentRequest` records every ask.** The row carries the blocked turn's own text,
  so the resumed finalization runs on the same content that was blocked (stable idempotency
  hash and turn correlation id), and its attempt counter is the loop guard — AgentRecall
  asks at most once per turn and never depends on an undocumented host signal. Stale rows
  expire, so a chat that ended mid-exchange cannot mute enforcement for the next turn.
- **Configuration:** `AgentRecall.JudgmentEnforcementMode` (`Substantive` default, `Always`,
  or `Off` for the previous never-block behaviour),
  `AgentRecall.JudgmentEnforcementMinTurnCharacters` (structural size floor, default 200),
  and `AgentRecall.MaxJudgmentRequestsPerTurn` (default 1).

### Changed
- **Finalization diagnostics distinguish the reasons nothing was captured.** New decision
  sources `NoJudgmentSupplied` and `JudgmentRetryExhausted` separate "nobody judged this
  turn" from "asked and never answered", and both stay distinct from a real
  `SemanticCaptureJudge` + `Skip` rejection. The old "Semantic capture judge unavailable"
  wording is gone: it read like an external judge service was down, and there is no such
  service — AgentRecall makes no model or network calls. `capture-status` and the
  `capture_status` tool now also report `awaiting_judgment` while an ask is outstanding,
  instead of answering with the previous turn's decision.
- **A resumed turn no longer files a second, contradictory finalization.** A blocked turn
  says more once it resumes, so its content hash moves; an unjudged finalization of a turn
  that already carries a recent judged verdict now returns that record instead of writing a
  new one.
- **The Stop payload's `last_assistant_message` is read when present**, and a payload that
  carries only one half of the exchange fills the other half from the transcript rather
  than finalizing a half-empty turn.
- **Mutation testing no longer slows down every push/PR.** The `mutation-test` job in
  `.github/workflows/ci.yml` now runs Stryker with `--since:origin/main`, mutating only
  files changed vs `main` instead of the whole `AgentRecall.Core` project. A new
  `mutation-nightly.yml` workflow (cron + `workflow_dispatch`) runs the full, unscoped
  mutation suite once a day and uploads its own report artifact.

## [2.7.2] - 2026-08-10

### Fixed
- **`finalize-turn` rejected valid `target_existing_rule_id` values.** `TurnPayload`'s
  JSON parser only accepted the id as a literal JSON integer; a whole-number double (e.g.
  `43.0`, which some JSON encoders always emit for numeric fields) silently parsed to
  `null`, so `ReinforceExisting`/`SupersedeExisting` verdicts failed validation with
  "missing target_existing_rule_id" even though a valid id was sent. `AsInt` now also
  accepts whole-number doubles and numeric strings.

## [2.7.1] - 2026-08-08

### Added
- **Mutation testing runs in CI.** A `mutation-test` job in `.github/workflows/ci.yml`
  runs `dotnet stryker` against `AgentRecall.Core` on every push/PR and uploads the
  HTML report as a build artifact. It's exploratory (`continue-on-error: true`), not a
  merge gate, since the existing `stryker-config.json` sets `break: 0`.

## [2.7.0] - 2026-08-06

### Added
- **`report_bad_rule` MCP tool and `agentrecall rules report-bad <id>`.** Lets a client
  archive a rule on the spot the moment it turns out to be wrong, corrupted, or otherwise
  unusable, instead of waiting on the outcome-tracking pipeline's gradual confidence decay
  to eventually retire it.

### Changed
- **Capture-suggestion prompts are harder to miss.** The Turn Memory Summary's "Awaiting
  your approval" section and the interactive "possible lesson detected" prompt are now
  wrapped in `---` separators with a single leading 🚨, instead of blending into surrounding
  text.

## [2.6.0] - 2026-08-03

### Added
- **`agentrecall claude-code init`.** A plain alias for wiring AgentRecall's
  recall/capture hooks and `CLAUDE.md` guidance into a Claude Code project —
  the same wiring `devcontainer init` already did, but under a name that
  doesn't read as container-specific and is easy to skip in a project that
  isn't containerized. `devcontainer init` is unchanged and still does this
  wiring too, plus dev container scaffolding with `--create`.
- **`agentrecall init` now points at `claude-code init`.** A one-line hint
  printed right after the database is created, so the very first command a
  new user runs surfaces the follow-up step Claude Code integration needs.

### Changed
- **`doctor` now warns about missing hook wiring in any git repository**, not
  only in projects that had already partially opted in. Previously, a project
  that had never created a `.claude` directory or `CLAUDE.md` got a clean "All
  checks passed" from `doctor` even though recall/capture were never wired —
  the check silently skipped itself precisely in the situation a project in
  that state would never escape on its own. It now reports a warning (pointing
  at `claude-code init`) inside any git repository instead, and `--fix` wires
  it automatically as before.

## [2.5.0] - 2026-07-31

### Changed
- **Every automatic capture now defaults to pending your approval.** Previously a
  high-confidence (`>=0.80`) capture from the Stop hook went straight to `Active` with no
  human review. Now it's parked `Pending` and surfaced in the Turn Memory Summary under
  "Awaiting your approval" with the rule's description, and resolved by the user's chat
  reply — yes/no for one rule, or "yes to all" for every rule still pending in the
  conversation. An explicit user save still bypasses this (no redundant second ask), and
  `AgentRecall.InteractiveMemoryMode: Silent` remains the global switch back to the old
  direct-to-Active behavior.

### Added
- **`resolve_pending_capture` MCP tool.** Lets the model act on the user's reply:
  `approve`/`reject` for a single `rule_id`, or `approve_all`/`reject_all` for every rule
  pending in the chat, scoped by the host's `session_id` (now threaded through from the
  Stop-hook payload and stamped on each pending rule) so "yes to all" only ever resolves
  the current conversation.
- **`agentrecall rules approve|archive --all-pending [--session <id>]`.** The CLI-terminal
  equivalent of "yes to all" / "no to all", for resolving a backlog of pending captures by
  hand.

## [2.4.1] - 2026-07-30

### Fixed
- **Stale token count/explanation from `inject_context` after conflict resolution.** When two
  already-injected rules conflicted (e.g. "use unit tests" vs. "use integration tests" for the
  same subject), the policy that resolves the conflict and drops the losing rule ran *after*
  the rule count and token total were computed, so the `explanation` and `tokens_used` fields
  returned by the `inject_context` MCP tool (and the CLI's own printout) still counted the
  dropped rule. Both are now recomputed from the rules that actually survived.

## [2.4.0] - 2026-07-28

### Added
- **An `agentrecall doctor` command.** Checks database/schema readiness (running the same
  additive schema reconciliation `init` uses), whether the .NET tools directory is on PATH,
  whether a project's Claude Code hooks are fully wired (only checked when the project already
  has a `.claude` directory or `CLAUDE.md`, so it never reports a false problem outside a
  Claude Code project), and whether a newer version is published on NuGet. Read-only by
  default; `--fix` repairs what's safely repairable — adding the tools directory to PATH,
  rewiring hooks via the existing dev-container scaffolder — and self-upgrades via
  `dotnet tool update --global AgentRecall` when a newer version is available. `--json` for
  machine-readable output; `--offline` skips the network version check; `--project <path>`
  points the hook check at a directory other than the current one.

## [2.3.1] - 2026-07-27

### Added
- **Test coverage for the in-place `CLAUDE.md` guidance refresh.** `EnsureClaudeMdGuidance`
  already refreshed a stale guidance block in place on re-run (preserving surrounding user
  content, never duplicating the block), but the `Updated` outcome had no test asserting it.
  Added one.

### Changed
- **Clarified the dev-container README section.** It previously read as if re-running
  `agentrecall devcontainer init` were only ever a no-op; it now says explicitly to re-run it
  after every AgentRecall upgrade, since that's when a drifted guidance block actually gets
  refreshed.

## [2.3.0] - 2026-07-27

### Added
- **A host-judged document-opportunity feature.** A new `doc_opportunity` object, a sibling of
  `judgment` on the `finalize-turn` payload, lets the session model offer generating a durable
  document — an incident report, RFC, design proposal, ADR, postmortem, or runbook — using the
  same host-supplied-verdict architecture as the semantic capture judge, so AgentRecall never
  calls an LLM or the network itself. Offering only surfaces a short, bounded pointer in the
  Turn Memory Summary; the reason and key points stay out of the model-visible surface until the
  user explicitly agrees. Only then does `agentrecall document write --type <T> --title
  "<title>"` (body piped on stdin) generate the file, under a type-based subfolder
  (`docs/rfcs`, `docs/incidents`, ...) auto-named from the date and a slugified title. A naming
  collision auto-suffixes (`-2`, `-3`, ...) rather than overwriting; `--force` opts into
  overwriting in place. `agentrecall document status` reports the mode and last offered
  candidate on demand.

## [2.2.6] - 2026-07-24

### Added
- **The semantic capture judge now reflects on friction, not only explicit signals.** Before
  defaulting to `Skip`, its own instructions now ask it: "if I redid this turn knowing what I
  know now, what would I have needed to be told upfront?" Friction that was never voiced as a
  correction (backtracking, a wrong assumption walked back, avoidable rework) can now surface
  as a lesson through this reflection alone. A finding from it is always reported with the new
  `SelfIdentifiedFriction` reason and is always parked as a pending suggestion, never
  auto-captured — enforced in the decision mapper regardless of reported confidence, since it's
  the model's own self-assessment rather than an observed external signal.

## [2.2.5] - 2026-07-24

### Fixed
- **Pending rules can now be recognized and reinforced.** The `UserPromptSubmit` and
  `PreToolUse` hooks now surface a capped number (default 1, `HookPendingCap`) of
  task-relevant Pending rules by default (`HookIncludePending` now defaults to
  `true`), each rendered with a `(pending — not yet approved)` marker so it reads
  distinctly from an approved suggestion. Previously Pending rules were invisible
  to later turns, so a repeated suggestion could never be recognized and
  reinforced toward auto-promotion — it just re-suggested a duplicate or sat stuck
  until manual approval. The semantic judge's own instructions now also say to
  emit `ReinforceExisting` against a visible pending match instead of duplicating
  it. Other callers (`inject_context`, `get_project_rules`, `search_rules`) are
  unaffected — still explicit-opt-in and uncapped where they already were.
- **The career-impact "no impact" pointer now points at the right place.** It
  previously read as if the last turn specifically was unremarkable; it now says
  to run `career status`, since an empty store usually means the pack isn't
  installed or is running in Silent mode.

### Added
- **Stryker mutation testing setup** (`.config/dotnet-tools.json`,
  `stryker-config.json`) plus expanded test coverage across capture,
  career-impact, and turn-finalization paths.

## [2.2.4] - 2026-07-21

### Added
- **A `Dockerfile` for the MCP server.** Multi-stage build that publishes `AgentRecall.Cli` for
  `net10.0` and runs `agentrecall mcp` as the entrypoint, for platforms that build/run the server
  from a container rather than the published NuGet tool.
- **Test coverage in CI.** The test step now collects coverage via coverlet's `XPlat Code Coverage`
  collector and uploads it to Codecov; the README carries a coverage badge alongside CI status.

### Changed
- **README reorganized.** Added a table of contents, consolidated four scattered configuration
  tables into one, and merged repeated capture-related sections (Turn Finalizer, Semantic Capture
  Judge, Outcome-aware capture, Source/Outcome-Aware Capture) into a single Capture pipeline section.
  No content was dropped, only de-duplicated.

### Fixed
- **The career-impact turn-summary pointer now hints at what triggered it.** It previously read the
  same fixed sentence ("possible Staff-level impact detected…") regardless of cause. It now
  parenthesizes the detector's top matched reason (e.g. "involves a migration", "has a security
  dimension") — data the candidate already carried but the pointer discarded.

## [2.2.3] - 2026-07-19

### Added
- **Rule delete capability.** Rules could previously only be archived (soft-retired, kept in the
  database with an `Archived` status). `IRuleLifecycleService.DeleteAsync` now permanently removes
  a rule's row, exposed as `agentrecall rules delete <id> [--force]` and the `delete_rule` MCP tool.
  Deleting a rule that is currently in force (`Active` or `Promoted`) requires `force=true`; other
  statuses delete without it. A `RuleDeleted` event is recorded for the audit log either way.

## [2.2.2] - 2026-07-17

### Fixed
- **`resolve_rules` no longer clobbers a rule's own rationale.** The policy decision reason was
  written into the guidance node's `reason` key, overwriting the rule's own stored rationale. The
  decision rationale now goes under a distinct `decision` key so both survive.

### Changed
- **`UserPreferenceRecognizer` no longer decides which language to reply in.** Deciding a default
  language (English vs. Persian) belongs to the model, not this deterministic recognizer — an
  earlier heuristic could even store the opposite of what was asked (e.g. "reply in English, not
  Persian" was saved as "reply in Persian"). A stated language preference is now captured verbatim
  as a general user preference instead of being classified into a decided default.
  - Removing that dimension also made `FeedbackService`'s supersede-on-conflict recommendation
    permanently unreachable (every other communication dimension always normalizes to the same
    fixed guidance text, so language was the only one that could ever produce a real conflict).
    That dead code path is removed too.

## [2.2.1] - 2026-07-15

### Fixed
- **File/Directory-scoped rules are now surfaced by the PreToolUse hook.** The relevance ranker
  only rewarded a rule whose `ScopeValue` exactly equalled the request's — the repository — so a
  `File`- or `Directory`-scoped rule scored `0.0`, *below* a global rule, and could never surface
  when recall was keyed on a file. It now also matches a rule whose file/directory scope contains
  a changed file (separator- and prefix-tolerant, so a repository-relative stored path matches an
  absolute host path).
- **PreToolUse retrievals join the end-of-turn summary.** The hook derived its turn correlation id
  from the file path, which never matched the `(cwd, prompt)` id the UserPromptSubmit and Stop
  hooks use — so its retrievals were dropped from the per-turn summary entirely (a non-empty,
  non-matching id also skipped the time-window fallback). It now reads the turn's prompt from the
  transcript like the Stop hook, and leaves the id null when none is available.
- **No more per-write inflation or duplication within a turn.** A rule surfaced earlier in a turn
  (by an earlier write, or by UserPromptSubmit) is now excluded from later writes via the new
  `ContextRequest.ExcludeRuleIds`, so it is neither re-injected (context bloat) nor re-counted as
  used (which had skewed the "which rules help" telemetry by however many files a turn touched).
- **Concurrent hook processes no longer drop writes.** The SQLite connection now uses a busy
  command timeout (wait-and-retry instead of an immediate `SQLITE_BUSY`) and WAL journaling, so the
  MCP server and per-write hook processes writing the one local database serialise rather than fail.
- **The "fetched N rules" status line no longer pollutes the model's context.** It moves to the
  user-facing `systemMessage` channel; only the rule text is injected via `additionalContext`.

## [2.2.0] - 2026-07-14

### Added
- **PreToolUse recall hook.** AgentRecall now injects rules at the moment a file is written, keyed
  on the file's path and the code being written — not only on the turn's opening prompt. This closes
  the gap where a high-level request (e.g. "implement login feature") carries no signal that a
  matching file is coming, so a convention scoped to that file was never surfaced by prompt-time
  recall alone. Recall keyed on the actual artifact surfaces it right before the write.
  - The hook is deterministic: Claude Code runs it before every file-mutating tool call, so recall
    no longer depends on the model choosing to call an MCP tool. It is scoped via a matcher to the
    file-mutating tools (`Edit`, `Write`, `MultiEdit`), so it is never spawned for reads, searches,
    or shell commands.
  - It is targeted, not always-on: the request is built from the file path and a bounded snippet of
    the new code, and when no rule is relevant to that file the hook injects nothing. Context is
    delivered through `hookSpecificOutput.additionalContext` (the only PreToolUse channel the model
    sees), and code extraction tolerates both the tool payload shapes a host may send.
  - `agentrecall hook pre-tool-use` is the new CLI entry point, and `agentrecall devcontainer init`
    wires the hook into `.claude/settings.json` automatically alongside the existing
    UserPromptSubmit (recall) and Stop (capture) hooks.

## [2.1.0] - 2026-07-11

### Added
- **Standing (always-apply) rules.** A rule can now carry an `AlwaysApply` delivery flag that makes
  AgentRecall inject it on **every** task rather than only when it matches by relevance. This fixes
  universal constraints — style, tone, process, and quality rules such as "don't leave unnecessary
  comments" — being captured correctly yet never surfaced, because relevance retrieval filters out
  any rule that shares no keyword with the task. `AlwaysApply` is orthogonal to `ScopeLevel`: it
  governs *how* a rule is delivered (standing vs. relevance-gated), not *where* it is true.
  - Standing rules bypass the relevance floor and are delivered as a small, capped (5 per prompt),
    high-salience **must-follow** band ordered ahead of relevance-ranked rules; prohibitions surface
    as warnings. Rules beyond the cap fall back to ordinary relevance gating, so the band stays few
    and prominent instead of degrading into ignored guidance.
  - Universality is classified at capture time, so one correction is enough: the semantic capture
    judge flags a universal constraint via `always_apply`, preferences (`UserPreference` /
    `CommunicationPreference`) are standing by nature, and a repeated correction promotes an
    existing rule to standing as a backstop.
  - Standing rules are surfaced on capture (`[standing — applies every turn]`), in the Turn Memory
    Summary (`[standing]`), and in the model-facing rule guidance (`always_apply`).
  - Contextual and project-scoped rules are unchanged: still retrieved by relevance and bound to
    their scope. The `AlwaysApply` column is backfilled on existing databases by the additive schema
    reconciler. See the README "Standing Rules" section for guidance on good vs. overbroad standing
    rules.

### Changed
- The compact Turn Memory Summary now labels AgentRecall's own end-of-turn capture as
  `auto-captured` (previously `captured`), so the count no longer reads as "nothing was saved this
  turn" when the model saved rules directly via the MCP tools mid-turn.

## [2.0.0] - 2026-07-09

### Breaking
- Automatic Stop-hook capture no longer works from turn text alone. Because the semantic judge
  decides capture and there is no keyword fallback, a Stop hook that does not carry a host-supplied
  `judgment` now captures nothing (earlier versions auto-captured from the turn's text). Wiring the
  host verdict — or enabling a live judge provider — is required to restore automatic capture; set
  `AgentRecall.CaptureJudgeMode = Off` to disable the path deliberately. Existing rules are
  unaffected.

### Changed
- **Stop-hook capture is now decided by a semantic capture judge, not keyword heuristics.**
  The keyword/regex pipeline that extracted candidates and decided capture
  (`MemoryWorthinessClassifier` → `CaptureDecisionPolicy`/`AdaptiveWorthinessPolicy`, fed by
  `TurnCandidateExtractor` and the source/outcome classifier) no longer drives the finalize-turn
  path. The model decides whether a turn holds memory-worthy content; AgentRecall validates the
  verdict and persists it. Incidental keywords ("validation", "scope", "auth", …) can never
  cause a capture, and there is **no keyword fallback** — when the judge is unavailable the turn
  is skipped. This fixes off-topic sentences, assistant prose, tool/skill docs, and stale chat
  fragments being captured as rules.
- The judge's verdict is supplied by the host on the `finalize-turn` payload as a `judgment`
  object (AgentRecall makes no network or LLM calls). A new `ICaptureJudge` seam obtains it;
  `CaptureJudgeValidator` enforces the strict-JSON contract (required fields per decision,
  confidence range, bounded lengths, no raw prose) and `CaptureJudgeDecisionMapper` applies the
  confidence thresholds (`≥ 0.80` capture, `0.55–0.79` suggest, `< 0.55` skip; explicit save
  always captures; explicit do-not-save always skips; duplicates reinforce; explicit supersede
  replaces).
- Explicit user saves are honored even when the rule is narrow, project-local, stylistic, or a
  preference; the rule is normalized into clean, bounded form. Reviewer corrections, observed
  agent failures, repeated mistakes, confirmed conventions, and documentation-backed corrections
  are captured; documentation/tool instructions/command output/logs read during a turn are not
  memory on their own.

### Added
- `AgentRecall.CaptureJudgeMode` configuration option (`Semantic` default, or `Off` to disable
  automatic Stop-hook capture). `IFeedbackService.AddJudgedAsync`, which persists a judged rule
  (with deduplication and event recording) without re-running the keyword classifier or decision
  policy.
- `capture-status` and `turn-summary` now report the decision source (`SemanticCaptureJudge`),
  the decision, the judge's exact capture reason, and the confidence, on the CLI (text and JSON)
  and the `capture_status` MCP tool. The judge decision metadata is persisted on the turn
  finalization (backfilled on existing databases by the additive schema reconciler).
- `get_rule` MCP tool: fetch a single stored rule by id (exact lookup, any lifecycle status) to
  verify a rule's stored content without a ranked `search_rules` query.

### Retained
- `cleanup pending-noise` and the `StopHookCandidateGate` quality screen are kept: they still
  find and archive noisy Pending rules created before the judge existed. Manual
  `agentrecall feedback add` continues to work through the existing explicit flow.

## [1.5.0] - 2026-07-08

### Changed
- **Stop-hook capture is now source/outcome-aware.** The keyword screening that gated captures
  is replaced by a deterministic classifier that labels each candidate's source and outcome
  before capture. Documentation, tool/skill instructions, command output, and logs are no
  longer captured merely because they were read — they become memory only when paired with an
  observed agent failure, an explicit save, or a confirmed repository convention. Classification
  reads structured activity metadata first (skill-doc / tool-doc / command-output / log-output)
  and falls back to a small set of compiled, timeout-guarded regex pattern groups
  (`SourceDocumentInstructionPattern`, `ToolOrSkillInstructionPattern`, `CommandOutputPattern`,
  `LogOutputPattern`, `AssistantMetaProsePattern`, `ExplicitSaveIntentPattern`,
  `ExplicitDoNotSaveIntentPattern`, `ObservedFailureOrCorrectionPattern`,
  `RepositoryConfirmationPattern`). New structured skip reasons `SourceDocument`,
  `ToolOrSkillInstruction`, `CommandOutput`, and `LogOutput` are surfaced alongside the existing
  ones. Because corrections ("use X instead of Y") and explicit saves ("save this: …") are
  recognised before any source shape, user guidance that merely starts with "Use" is never
  mistaken for documentation.
- **Do-not-save detection is now English-only.** The first release of source/outcome-aware
  capture drops the Persian/Finglish do-not-save and meta-prose vocabulary from the Stop-hook
  gate; the classifier and the scaffolded guidance are English-only and deterministic.

## [1.4.0] - 2026-07-07

### Added
- **Stop hook capture hardening.** A deterministic quality gate now screens every Stop-hook
  capture candidate before a rule is created, so assistant chatter never becomes memory. It
  skips assistant prose and meta commentary ("one thing worth saving…", "want me to save
  it?", "the Stop hook may have captured it"), malformed triggers built from conversation
  fragments, and vague candidates with no actionable guidance — each recorded with a
  structured reason (`AssistantProse`, `MalformedTrigger`, `TooVague`, `MissingAction`, …)
  instead of being parked as noisy Pending rules for you to clean up later.
- **Explicit do-not-save is honoured across the board.** An explicit do-not-save instruction
  in English, Persian, or Finglish (`don't save this`, `این رو ذخیره نکن`, `save nakon`)
  hard-skips the turn — no rule, no Pending candidate. When a turn carries both a do-not-save
  and a save request, the most recent instruction wins and ties prefer do-not-save. The skip
  reason is surfaced by `capture-status`, the Turn Memory Summary, and a structured activity
  record (with a capped candidate excerpt, never the transcript).
- **`cleanup pending-noise` command.** Finds and archives noisy Pending rules created before
  hardening, using the same filters. Dry run by default; `--apply` archives (never
  hard-deletes), and it never touches Active/Promoted, user-modified, or clean rules
  (`--json`, `--tag <tag>`, `--status <status>`).

### Changed
- The Stop-hook `CLAUDE.md` scaffold now states the capture-hardening contract: if the user
  says not to save something, AgentRecall must not capture it, and assistant explanations are
  never turned into rules.

### Fixed
- **The turn-payload and pull-request comment parsers are now genuinely tolerant of malformed
  input.** Both honour their documented "never throws" contract even when a JSON field carries
  the wrong type (a number where a string is expected) or the payload is an array/scalar rather
  than an object — surfaced by property-based fuzzing. A wrong-typed field is treated as absent
  instead of throwing into the Stop hook.

### Security
- **Bumped the transitive SQLite native bundle to the patched 3.x line.** EF Core pulls
  `SQLitePCLRaw` 2.1.x, whose `e_sqlite3` native library is flagged by CVE-2025-6965
  (GHSA-2m69-gcr7-jv3q) with no patched 2.1.x release. Pinned `SQLitePCLRaw.bundle_e_sqlite3`
  to 3.0.3 (SQLite 3.50.3, past the fix) in the projects that use SQLite, clearing the advisory
  while remaining compatible with EF Core 8 and 10.

## [1.3.0] - 2026-07-05

### Added
- **Career-impact seed pack.** A new opt-in pack surfaces Staff-level engineering impact —
  evidence, metrics, stakeholders, architectural decisions, and promotion-ready
  achievements — from the work you already do. A cheap deterministic detector runs at
  end-of-turn only when the pack is installed and `CareerImpactMode` is not `Silent`,
  printing a compact summary for significant work and staying silent for trivial turns.
  The full promotion journal is generated on demand via the career commands, while the
  model-visible hook path emits only a short Turn Summary pointer so the detector stays
  low-token in the loop. Like every pack, it is never installed automatically.

### Changed
- **Review-acceptance intent is matched with regexes instead of fixed phrases.** Both
  capture paths — the Stop-hook `CaptureHook` and the turn finalizer's
  `TurnCandidateExtractor` — now share a single `ReviewAcceptanceIntent` helper, so
  natural phrasings with intervening words (e.g. "apply the reviewer's second comment",
  "do exactly what the reviewer said", "per the review feedback") force a rule Active
  consistently. The two paths previously disagreed because only one had been migrated off
  the phrase list. Save/remember intent and verb-trails-noun phrasings stay as exact
  matches; the broad ranking keyword lists remain substring lists.
- **Dev container scaffolding is opt-in when a project has none.** `devcontainer init`
  always wires the environment-agnostic pieces (the recall/capture hooks and `CLAUDE.md`
  guidance). When a `devcontainer.json` already exists it still wires into it, but a
  project without one is no longer handed a full dev container it did not ask for — the
  command defers and prints the single command to generate one. A new `--create` flag
  opts into scaffolding the manifest and post-create script.
- The career-impact promotion note reads as natural prose ("Staff-level engineering work
  that involves a migration and makes an architectural decision.") rather than a
  PascalCase enum name followed by a lowercase fragment.

## [1.2.0] - 2026-07-03

### Added
- **Built-in seed packs.** AgentRecall can now install curated, opt-in starter rules that
  give an agent useful engineering instincts before a project has accumulated its own
  memory. The first pack, `tidy-first`, ships ten paraphrased, conditional rules for
  separating behaviour-preserving cleanup from behaviour change (no copied book text). New
  commands: `agentrecall seed list | show <pack> | install <pack> [--active] [--suggested]
  [--force] | remove <pack> [--force] | status` (all support `--json`). Packs are never
  installed automatically. Installing a pack is the opt-in, so its rules go in **Active** at
  moderate confidence (~0.65) from day one; `--suggested` is the conservative mode that
  installs them as Pending for manual approval.
- Seed rules are stored as normal rules but marked seed-derived: additive
  `RuleSource.BuiltInSeed`, `RecallRule.SeedPack` / `SeedRuleKey`, and
  `CaptureReason.BuiltInSeed` (enum values persist as strings, so the change needs no
  migration). Installs are idempotent (no duplicates, never overwrite user edits); removal
  archives a pack's rules while preserving ones the user edited or promoted, and a reinstall
  will not resurrect a removed rule without `--force`.
- Seed-aware retrieval: seed rules rank below locally learned rules, lose conflicts against
  repository conventions, are never injected as "must-follow", and are capped at two per
  prompt unless the task itself is about tidying/refactoring. Their confidence evolves like
  any rule — a small, capped bump for repeated uneventful use, larger moves on explicit
  acceptance or rejection. Used seed rules show a `[seed]` marker in the Turn Memory Summary,
  and installing a pack emits an activity notice. The scaffolded `CLAUDE.md` and the README
  document that seed rules are starter guidance, not project truth.

## [1.1.0] - 2026-07-02

### Added
- **Explicit user preferences are captured as first-class memory.** When the user states
  a durable preference for how the assistant should communicate — answer length,
  explanation depth, language, prompt format, how often to ask questions — AgentRecall now
  recognises it (English and Persian) and stores it as a `UserPreference` /
  `CommunicationPreference` rather than a low-confidence repository convention. A new
  deterministic `UserPreferenceRecognizer` detects the preference, refuses unsafe ones
  (e.g. "always agree even if I'm wrong"), and normalizes raw phrasing into durable,
  bounded guidance — no LLM, no embeddings. New `RuleCategory.UserPreference` /
  `CommunicationPreference` and `CaptureReason.ExplicitUserPreference` (enum values persist
  as strings, so the change is additive with no migration).
- Explicit safe preferences are auto-captured with high confidence (≈0.90) regardless of
  the auto-approve posture — the user's own word is enough — and are scoped to the user
  (Global), never to a repository. A newer preference about the same dimension (e.g. answer
  length or language) as an older one raises a `Supersede` lifecycle recommendation instead
  of silently keeping both active.
- `rules explain` now shows the rule `Type` (category) and `Scope`; the activity log and
  Turn Memory Summary describe a captured preference as "captured 1 user preference". The
  scaffolded `CLAUDE.md` and the README document the behaviour.

## [1.0.0] - 2026-06-29

### Added
- **Turn Memory Summary.** After a turn is finalized, AgentRecall prints one aggregated
  summary of what it did that turn — rules used, captured, suggested, and skipped, plus
  Interactive Memory remember/ignore decisions and any recoverable errors — instead of
  many scattered notices. A new `AgentRecall.TurnSummaryLevel` (`Silent` | `Compact` |
  `Detailed`, default `Compact`) controls it, independently of `ActivityNoticeLevel`. The
  Stop hook now emits this summary; `agentrecall turn-summary --last` (with `--json`,
  `--detailed`, `--compact`) reports it on demand. The summary is human-visible only, is
  never injected into model context, and is bounded even in detailed mode.
- Turn correlation: retrieval activity (UserPromptSubmit) and capture activity
  (Stop/finalize-turn) now share a deterministic turn id derived from the working
  directory and prompt, so a turn's used and captured rules join into one summary without
  fragile cross-process state. When no id is available the summary falls back to a
  conservative time window. Additive schema columns (`AgentRecallActivity.TurnId`,
  `TurnFinalization.TurnId`) are backfilled by the schema reconciler.

### Changed
- `capture-status` now points to `agentrecall turn-summary --last` for the full per-turn
  activity, keeping its own output focused on the capture decision.

## [0.13.0] - 2026-06-28

### Performance
- Context retrieval no longer does an N+1 write: `RecordRetrievalAsync` batches all
  RuleApplied events and LastUsedAt bumps into bulk writes (new `AddRangeAsync` /
  `UpdateRangeAsync` on the repository). The UserPromptSubmit hook initializes the database
  once per process instead of re-running the schema reconciler on every prompt. Feedback
  deduplication and keyword search now filter by scope and status in the database (new
  `IRecallRuleRepository.QueryAsync`) rather than loading the full rule table into memory.

### Changed
- **Rule status taxonomy is now consistent.** `Superseded`, `Retired`, and `Archived` are
  the dead set, excluded from search (previously `Retired` rules still appeared),
  deduplication, the policy engine, and context injection via a shared `RuleStatusSets`.
  `Draft` and `Pending` remain searchable. The README lifecycle description now lists every
  `RuleStatus`.
- Search ranking is documented as keyword + concept based; the only embedding provider is a
  no-op, so no embeddings are computed and no external service is contacted by default.
- `agentrecall rules list` accepts `--status <status>` to filter by lifecycle state.

### Added
- Feedback intake validation: empty-after-trim feedback is rejected with a clear error, and
  feedback/task text is capped (`AgentRecall.FeedbackMaxLength`, `FeedbackMaxTaskLength`).
- Log import is streamed line-by-line with a size cap (`AgentRecall.LogImportMaxBytes`) and a
  per-line length cap (`AgentRecall.LogImportMaxLineLength`) instead of reading the whole
  file into memory.
- Repository layer gained `DeleteAsync` and an `OnUpdating` hook that stamps `UpdatedAt`
  consistently, plus an `ITransactionRunner` so memory compression runs atomically.

### Fixed
- An MCP tool that throws now returns a tool-level error (`isError: true`) instead of
  corrupting the JSON-RPC stream with an internal error; the exception is logged to stderr.
- Compiled regexes carry a match timeout, and the search tokenizer dedupes with a HashSet.

### Internal
- `CommandRouter` is now a thin `ICommand` dispatch table rather than a large switch, with
  command groups moving into their own files. The duplicate `Policy.RuleConflictDetector`
  was renamed `PolarityConflictHeuristic` to disambiguate it from the authoritative
  `Conflicts.RuleConflictDetector`. Tags remain a comma-separated string (documented
  limitation), and the install scripts check for `dotnet` and resolve the tools directory
  rather than hardcoding it.

## [0.12.0] - 2026-06-28

### Added
- **Interactive Memory** turns an uncertain capture into a visible, lightweight choice.
  It surfaces the existing capture decision (`AutoCapture` / `SuggestCapture` / `Skip`)
  without making a parallel flow and without re-classifying worthiness: a high-confidence
  lesson is still captured automatically with a notice, a skip never asks, and only an
  ambiguous `SuggestCapture` is surfaced. With a terminal attached it shows a `[y]/[n]/[v]`
  prompt — remember (approve), ignore (archive), or view full details before deciding again.
- `AgentRecall.InteractiveMemoryMode` (`Auto` default | `Ask` | `Silent`) controls whether
  AgentRecall asks. It is distinct from `ActivityNoticeLevel` (which controls how loud
  notices are). `Ask` also downgrades a borderline auto-capture to a question; `Silent`
  never prompts. An invalid value falls back to `Auto` with a warning.
- Non-interactive surfaces never block. Hooks, pipes, and MCP park the suggestion as a
  Pending rule and name the follow-up command (`agentrecall rules approve <id>` /
  `archive <id>`). The MCP `capture_feedback` / `add_feedback` response carries
  `capture_decision`, `pending_rule_id`, and `suggested_actions: [approve, reject,
  view_details]` — no terminal prompt text.
- `agentrecall rules list --status <status>` filters by lifecycle status (e.g. `Pending`).
  Remembering or ignoring a suggestion records an activity notice (`SuggestionRemembered` /
  `SuggestionIgnored`) with the reason.

### Changed
- The generated `CLAUDE.md` and the README document Interactive Memory: never ask "Want me
  to save it?" — for a `SuggestCapture`, present AgentRecall's `remember` / `ignore`
  options; for an `AutoCapture`, simply notify; for a `Skip`, don't push.

## [0.11.0] - 2026-06-27

### Added
- **Outcome-aware capture** weighs not only a candidate's text but the evidence that
  produced it. A new `AdaptiveWorthinessPolicy` layers on top of the existing
  `MemoryWorthinessClassifier` and `CaptureDecisionPolicy` (it never replaces them):
  given a `CaptureContext`, it raises or lowers the capture decision so a generic lesson
  backed by a real observed agent failure is kept, while the same words with no evidence
  are skipped. Bare code facts are still never auto-captured, project conventions are
  still captured, duplicates reinforce, conflicts hold for review, and repeated
  corrections raise confidence. Deterministic — no LLM, no embeddings.
- `CaptureReason` (`ObservedAgentFailure`, `UserCorrection`, `AcceptedReviewComment`,
  `TestFailedThenFixed`, `RepeatedCorrection`, `LessonMined`, `ManualFeedback`,
  `ImportedFeedback`) and an evidence summary are persisted on the rule (additive
  columns, backfilled by the schema reconciler) and shown by `agentrecall rules explain`
  under `Captured because:` / `Evidence:`.
- When an observed failure elevates a generic observation, it is rewritten into a
  conditional, branch-preserving lesson (e.g. "When flattening nested template
  conditionals, preserve `{{else}}` semantics …") rather than a context-free imperative.
- The Turn Finalizer and Stop-hook capture path detect outcome signals in a turn ("that
  broke behavior", "no, preserve the else branch", "you changed semantics", "the review
  comment was applied", "tests failed because…", "this is the same mistake again") and
  feed them into the adaptive policy. Lesson mining marks candidates `LessonMined` or
  `RepeatedCorrection`, and the capture notice reads "captured 1 rule from an observed
  mistake."

### Changed
- Capture remains safe by default: with no outcome context supplied, every existing flow
  behaves exactly as before — the adaptive layer only adjusts decisions when evidence is
  present.

## [0.10.0] - 2026-06-26

### Added
- **Activity Notices** make AgentRecall visible by default. It now reports what it
  fetched, captured, skipped, resolved, mined, and recommended through a recognizable
  `🧠 **AgentRecall:**` badge. A new `AgentRecallActivity` ledger records each action
  (deduplicated by operation hash), and `agentrecall activity last` / `agentrecall
  activity list` (`--limit`, `--json`) read it back. JSON output keeps fields plain and
  confines Markdown styling to a `renderedNotice` field.
- Two settings control verbosity independently: `AgentRecall.ActivityNoticeLevel`
  (`Verbose` | `Normal` | `Silent`, default `Verbose`) for human-facing CLI/status
  output, and `AgentRecall.HookNoticeLevel` (`Normal` | `Silent`, default `Normal`) for
  the hook-injected notice. An invalid value falls back to the default with a warning.

### Changed
- The generated `CLAUDE.md` now carries an explicit **AgentRecall behavior contract**:
  a decision table routing "did you save anything?" / "what did AgentRecall do?" /
  "did the Stop hook capture anything?" to `capture-status --last-turn`, `activity
  last`, and `finalize-turn status`; the allowed check-then-report pattern; and the
  forbidden speculative non-answers. `agentrecall devcontainer init` now refreshes an
  older guidance block **in place** instead of appending a duplicate.

### Token safety
- The model-visible surfaces stay compact regardless of `ActivityNoticeLevel`: the
  UserPromptSubmit hook adds at most a single summary line (never detail bullets or
  rule text), `inject-context --no-notice` suppresses the human notice for machine use,
  and MCP capture responses carry only a one-line `rendered_notice`. Tests assert the
  hook output does not grow materially when notices are enabled.

## [0.9.2] - 2026-06-25

### Changed
- The scaffolded `CLAUDE.md` guidance now names the `capture_status` MCP tool as the
  thing to call (alongside the CLI commands) when answering whether AgentRecall
  captured anything, and to answer "never from memory".
- Added a README troubleshooting line: if Claude still says "the Stop hook may have
  captured it", update AgentRecall and re-run `agentrecall devcontainer init`.

### Notes
- Hardened `BehaviorContractTests` so the integration contract cannot silently
  regress after publishing: CLAUDE.md must mention `capture_status`, must tell the
  agent not to answer from memory, and must forbid the "I didn't manually call
  AgentRecall" non-answer; the published MCP tool list (over JSON-RPC) must include
  `capture_status`; and a golden test asserts `capture_status` returns a seeded
  captured rule verbatim.

## [0.9.1] - 2026-06-25

### Added
- Added a `capture_status` MCP tool and a shared finalization formatter so the CLI,
  the Stop-hook notice, and MCP all answer "did AgentRecall capture anything?" from
  the recorded decision, identically. `capture_status` returns the captured,
  suggested, skipped, and duplicate rule ids plus the source and timestamp.
- The last finalization result now carries its source and timestamp.

### Changed
- Strengthened the scaffolded `CLAUDE.md` guidance so the agent must check
  finalization status (`agentrecall finalize-turn status` or `capture-status
  --last-turn`) before answering capture questions, documents the captured /
  suggested / skipped / nothing-recorded answer patterns, and explicitly forbids
  speculating ("the Stop hook may have captured it") or asking "want me to save it?"
  except when a pending suggestion genuinely needs approval.
- Added a README troubleshooting section for the "the Stop hook may have captured
  it" answer, and documented the `capture_status` tool.

### Notes
- Behaviour-only release for the Claude integration contract; no change to how
  capture decisions are made. Added `BehaviorContractTests` locking the guidance,
  Stop hook, status command/tool, and output wording.

## [0.9.0] - 2026-06-25

### Added
- Added the Turn Finalizer: a single deterministic owner of capture for a
  completed turn. `agentrecall finalize-turn` reads a Stop-hook payload on stdin,
  extracts candidate lessons (user corrections and lessons the agent flags),
  classifies worthiness, detects duplicates and conflicts, and decides
  AutoCapture / SuggestCapture / Skip — so the agent no longer guesses whether the
  hook captured anything or asks the user to confirm.
- Added `finalize-turn status` / `--last` (and a `capture-status --last-turn`
  alias) to report the last finalization, `--json` for structured output, and a
  `--hook` mode that emits a non-blocking `systemMessage`.
- Added `TurnFinalizerEnabled`, `StoreTurnTranscript`, `MaxCandidatesPerTurn`,
  `MaxCandidateCharacters`, `FinalizerShowUserNotice`, and `SuppressDuplicateNotices`.

### Changed
- The scaffolded Stop hook now runs `agentrecall finalize-turn --hook`, and
  re-running `devcontainer init` upgrades an older `agentrecall hook capture`
  registration in place. The scaffolded `CLAUDE.md` guidance now tells the agent
  to report finalization status instead of guessing.

### Notes
- The finalizer reuses the existing worthiness classifier, rule extractor,
  deduplicator, conflict detector, and `FeedbackService` — it adds no parallel
  capture logic. It is deterministic, makes no LLM/embedding/network calls, never
  blocks Claude Code, and is idempotent. The raw transcript is not persisted unless
  `StoreTurnTranscript` is set.

## [0.8.0] - 2026-06-25

### Added
- Added a deterministic capture decision inside AgentRecall: every captured
  candidate now resolves to AutoCapture, SuggestCapture, or Skip, so the user is
  almost never asked whether to save a rule.
- Added `CaptureAutoConfidence` (default 0.5): the minimum confidence for
  AgentRecall to auto-capture a worthy lesson on its own. Below the bar (and with
  no explicit acceptance) the lesson is parked as a Pending suggestion to confirm.

### Changed
- The capture decision now weighs worthiness, confidence, the acceptance signal,
  duplicate detection, and scope as one final step, and carries the reason,
  confidence, scope, and notice. The capture hook, MCP tools, and CLI report the
  decision instead of asking. Explicit acceptance auto-captures regardless of
  confidence; a duplicate reinforces the existing rule rather than asking.

### Notes
- The decision reuses the existing worthiness classifier, rule extractor,
  deduplicator, and confidence scoring — it does not replace them. It is
  deterministic and makes no LLM, embedding, or network calls.

## [0.7.0] - 2026-06-24

### Added
- Added Project DNA: a deterministic, onboarding-ready summary of a repository's
  engineering personality (core principles, conventions, testing/architecture
  patterns, error handling, security, common mistakes, agent warnings, and
  stale/risky knowledge).
- Added `agentrecall dna` with `--markdown`, `--json`, `--top <n>`,
  `--scope-level`/`--scope-value`, and `--output <file>`.

### Notes
- Project DNA is local-only and makes no LLM, embedding, or network calls; the same
  inputs always produce the same output. It adds no new tables — it computes over the
  existing rules, events, outcomes, mined lessons, and lifecycle recommendations.

### Fixed
- Scaffolded Claude Code hooks now prepend the .NET global-tools directory to PATH
  (`PATH=$HOME/.dotnet/tools:$PATH agentrecall hook …`). Claude Code runs hooks via a
  non-login shell that may lack `~/.dotnet/tools`, which previously made a bare
  `agentrecall` fail with "command not found". Re-running `agentrecall devcontainer init`
  upgrades an older PATH-less hook command in place instead of appending a duplicate.

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
