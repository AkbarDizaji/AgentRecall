# Changelog

All notable changes to AgentRecall are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
