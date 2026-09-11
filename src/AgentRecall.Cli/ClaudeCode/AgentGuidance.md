## Memory (AgentRecall)

The `agentrecall` MCP server holds rules learned from past feedback. Recall and
capture are both wired as deterministic hooks: the UserPromptSubmit hook injects
the relevant rules automatically, and the Stop hook finalizes each turn through
`agentrecall finalize-turn`. AgentRecall's memory decisions come from a **semantic
capture judge**, not from keyword heuristics: the judge decides whether the turn
holds memory-worthy content, and AgentRecall only validates that decision and
persists it.

**{{contract-marker}}** — these instructions were written for that contract:
the semantic capture judge, Stop-hook judgment enforcement, and reported rule outcomes.
Hooks run the globally installed `agentrecall`, never this repository's source, so the
two can drift apart. Every injected context block names the contract the installed build
actually implements, in its heading: `## AgentRecall Technical Context (agentrecall
<version>, contract <n>)`. If that stamp is missing, or names a lower contract than the
line above, the installed CLI predates these instructions and cannot accept
`submit_capture_judgment` or `rule_outcomes` — nothing else reports that, and capture
silently stops happening. Run `agentrecall doctor` and tell the user what it says rather
than retrying the calls.

**You are that judge.** AgentRecall makes no model or network calls of its own — it
has no judge to fall back on and never guesses with keywords. So the Stop hook does
not decide capture on its own: it enforces that a judgment exists. If a substantive
turn reaches Stop with no verdict, AgentRecall declines to let the turn finish and
asks you for one; you call `submit_capture_judgment`, and the turn is finalized from
your verdict. A `Skip` verdict is a complete answer — most turns get one.

### Semantic capture judge

Incidental keywords never cause a capture. The judge decides; the system validates
and stores. Practical consequences for you:

- **If the user explicitly asks to save/capture/remember a rule, it is saved** — even
  when it is narrow, project-local, stylistic, or a preference. The judge normalizes
  it into a clean rule.
- **If the user says not to save something, it is not saved** — no active or pending
  rule.
- **A universal constraint becomes a standing rule.** When a correction is a style, tone,
  process, or quality rule that applies to every task (e.g. "don't leave unnecessary
  comments", "always run the formatter"), it is captured as an always-apply rule and
  injected on every turn — not only when it matches the task's keywords. Preferences are
  standing by nature; if you keep making the same correction, AgentRecall promotes that
  rule to standing on its own. Standing rules are marked `[standing]` in the summary.
- **Documentation, tool/skill instructions, command output, and logs you read are not
  memory on their own.** They become a rule only when the turn pairs them with an
  observed failure, a correction, or an explicit save — a documentation-backed
  correction can be captured.
- **Do not narrate the mechanism.** When the user asks what was saved, check
  `agentrecall capture-status --last-turn` or `agentrecall turn-summary --last` and
  answer from the actual recorded decision (see the do-not-say list below).
- If no judgment is ever supplied for a turn, nothing is captured for it and the turn is
  recorded as unjudged — AgentRecall never falls back to keyword capture. "Unjudged" and
  "you judged it as not worth keeping" are recorded as different things.

### You are the semantic judge — report your verdict every substantive turn

AgentRecall makes no model or network calls of its own: the judge in "semantic
capture judge" above is you. The Stop hook fires automatically after you finish, but
its payload carries no judgment unless you supply one yourself first. So on every
substantive coding turn — not on pure conversation — before you finish, decide the
verdict and report it:

    echo '<payload>' | agentrecall finalize-turn

where `<payload>` is:

    {
      "cwd": "<this turn's working directory>",
      "source": "model-self-judged",
      "prompt": "<the user's message for this turn, verbatim>",
      "assistant_response": "<a short summary of what you did/said this turn>",
      "judgment": {
        "decision": "Capture | SuggestCapture | Skip | ReinforceExisting | SupersedeExisting",
        "memory_type": "EngineeringLesson | RepositoryConvention | UserPreference | CommunicationPreference | DocBackedCorrection | ToolWorkflowConvention | ReviewLesson | CodeFact | NotMemory",
        "confidence": 0.0,
        "capture_reason": "ExplicitUserSave | ExplicitUserDoNotSave | ObservedAgentFailure | ReviewerCorrection | UserCorrection | RepositoryConvention | UserPreference | RepeatedMistake | DocBackedCorrection | DuplicateExisting | AssistantProse | SourceDocumentOnly | CommandOutputOnly | LogOutputOnly | CodeFact | NotReusable | Ambiguous | NotMemory | SelfIdentifiedFriction",
        "target_existing_rule_id": null,
        "normalized_rule": {
          "title": "...",
          "condition": "when ...",
          "action": "...",
          "avoid": "...",
          "because": "...",
          "scope": "...",
          "always_apply": false,
          "tags": ["..."]
        },
        "evidence": "...",
        "why_not_saved": "...",
        "dedupe_notes": "..."
      }
    }

Use `cwd` and `prompt` exactly as they were for this turn — the turn correlation id is
derived from both, and a mismatch stops this capture from joining the turn's earlier
recall in `turn-summary`.

Required fields depend on `decision`:

- **Skip** — `why_not_saved` is required. This is the common case: most turns are
  ordinary work with nothing durable to learn, and a real "nothing worth saving"
  verdict is what makes `agentrecall capture-status --last-turn` trustworthy, instead
  of the turn being recorded as one nobody judged.
- **Capture** / **SuggestCapture** — `normalized_rule.title`, `.condition`, and
  `.action` are the minimum; also fill `.because` and `.scope`, or `Capture` downgrades
  to a pending suggestion instead.
- **ReinforceExisting** — `target_existing_rule_id` and `dedupe_notes` are required (no
  new rule is created; the existing one's confidence is bumped instead).
- **SupersedeExisting** — `target_existing_rule_id` is required, plus a sound
  `normalized_rule` (all of title/condition/action/because/scope filled in).

Do this yourself even when nothing seems worth saving — a `Skip` verdict you report is
real signal; a turn you never report on is recorded as unjudged.

### When the Stop hook asks for a judgment, submit one

If a substantive turn reaches the Stop hook with no verdict, AgentRecall does not let it
finish: it returns a block whose reason asks for your judgment, and your turn resumes.
When that happens:

1. Call the `submit_capture_judgment` MCP tool. Its arguments are the same `judgment`
   fields shown above — `decision` and `capture_reason` are required, plus
   `normalized_rule` for Capture/SuggestCapture/SupersedeExisting,
   `target_existing_rule_id` for Reinforce/Supersede, and `why_not_saved` for Skip.
2. Do not redo the work, do not re-answer the user, and do not ask the user what to
   save. Judge the turn you just completed and submit the verdict.
3. `Skip` is a valid, expected answer for ordinary work. Submitting `Skip` is how a turn
   with nothing durable in it finishes cleanly.
4. Then finish your turn normally. AgentRecall finalizes from the submitted verdict, and
   the next Stop sees the turn as judged and lets it end.

AgentRecall asks at most once per turn: if the turn resumes and still submits nothing,
the turn is finalized as unjudged (recorded as asked-and-unanswered) and it ends. So an
unanswered ask costs the memory, not the turn — but there is no reason to leave it
unanswered, since reporting the verdict yourself (either way) is one tool call.

You can also call `submit_capture_judgment` before Stop fires — passing `prompt` and
`assistant_response` — as an alternative to piping the payload into `finalize-turn`.
Either route supplies the same verdict to the same judge seam.

**Before defaulting to Skip, reflect — don't only scan for explicit signals.** An
explicit correction, failure, or save request is the common case, but friction that
was never voiced as a correction is real too: backtracking, a wrong assumption you had
to walk back, rework caused by not knowing a constraint upfront. Before you write
`Skip`, ask yourself: "if I redid this turn from scratch knowing what I know now, what
would I have needed to be told upfront to avoid that friction?" If that surfaces a
concrete, reusable answer, report it as `SuggestCapture` with
`capture_reason: SelfIdentifiedFriction` — never `Capture` — since this is your own
self-assessment, not an observed external signal, so it always goes through the same
pending/human-review gate as any other ambiguous suggestion. If the reflection surfaces
nothing concrete, `Skip` as usual.

**Reinforce a matching pending suggestion instead of duplicating it.** Injected
context can include a rule marked `(pending — not yet approved)` — an earlier
suggestion that hasn't been reviewed yet. If what you are about to capture this
turn is the same lesson as one of those, emit `ReinforceExisting` with that
rule's `#id` (from its `Source:` line) rather than a new `Capture`/`SuggestCapture` —
this is how a repeated suggestion accumulates confidence toward auto-promotion
instead of sitting as a duplicate pending rule forever.

### Every capture defaults to pending your approval — ask, don't assume

Reporting `Capture` does not mean the rule is active yet. By default (unless the host
configured `InteractiveMemoryMode: Silent` to bypass this), every automatic capture,
even a high-confidence one, is parked Pending and surfaced in the Turn Memory Summary
under **Awaiting your approval**, with the rule's short description and a reply
instruction. When you see that section:

1. Relay it to the user in plain language, mentioning what the rule says, not just that
   "something is pending." This is a real yes/no question, not a rhetorical one.
2. Wait for their reply. Do not assume yes, and do not silently treat a Pending rule as
   if it were already active guidance.
3. On an explicit "yes" (for one rule) or "yes to all" (for every rule still pending in
   this chat), call `resolve_pending_capture` with `decision: approve` (plus `rule_id`)
   or `decision: approve_all`. On "no" / "no to all", call it with `decision: reject` /
   `reject_all` instead so the rule is archived, not left dangling.
4. `approve_all`/`reject_all` need no `session_id` from you; omit it and AgentRecall
   resolves to whichever chat most recently had something pending.

This follows the same non-blocking pattern as the document-opportunity flow below:
nothing on the CLI side halts waiting for input, and a `Skip` decision is never gated
(it created nothing to approve).

### Document opportunity — offer a document, never generate one yourself

Alongside `judgment`, the same `finalize-turn` payload accepts a sibling
`doc_opportunity` object. Supply it when the turn is a good moment to offer generating
a durable document — an incident just got resolved, an architecture decision was just
made, a design was just proposed, a runbook-worthy process just got worked out:

    "doc_opportunity": {
      "decision": "Offer | Skip",
      "document_type": "Incident | Rfc | Proposal | Adr | Postmortem | Runbook",
      "confidence": 0.0,
      "suggested_title": "...",
      "reason": "...",
      "key_points": ["...", "..."],
      "why_not_offered": "..."
    }

Required fields depend on `decision`:

- **Skip** — `why_not_offered` is required. This is the common case: most turns are
  not a document-worthy moment, and reporting `Skip` freely is what keeps
  `agentrecall document status` trustworthy instead of stale.
- **Offer** — `document_type` and `suggested_title` are the minimum; fill `reason` and
  `key_points` too so the eventual document has something to start from.

**Supplying `Offer` never writes a file by itself.** It only surfaces a one-line
pointer in the Turn Memory Summary (`Document Opportunity: ...`) — nothing is generated
or saved to disk from the judgment alone.

**The accept flow is conversational, not a CLI prompt.** When the pointer appears,
mention it to the user in plain language and ask whether they want the document
generated — never assume yes, never generate automatically. Only on an explicit
affirmative should you:

1. Draft the actual document content yourself — AgentRecall supplies no content, only
   the type/title/confidence signal.
2. Run `agentrecall document write --type <T> --title "<title>"` with the drafted
   Markdown piped on stdin (add `--turn-id <id>` when you have it, so the offered
   candidate is marked written instead of staying `Open`).

There is no blocking prompt on the CLI side — `document write` runs once, writes the
file, and returns; the yes/no only ever happens in chat.

**Do not narrate the mechanism.** When the user asks whether a document was saved,
check `agentrecall document status` (or `--json`) and answer from the actual recorded
state, the same discipline as capture-status questions above.

### Report how the injected rules fared

Recall has two halves. The injected block at the top of a turn is the first; the second
is saying whether those rules helped, and it is the only thing that moves a rule's
confidence. AgentRecall cannot observe it, so it asks — the injected block ends with a
`Retrieval id: <id>` line, and the end-of-turn ask names the rules still awaiting an
answer. Report them with the verdict, on the `finalize-turn` payload or as a
`submit_capture_judgment` argument:

    "rule_outcomes": [
      { "rule_id": 21, "retrieval_id": "<the injected retrieval id>",
        "outcome": "UserAccepted", "note": "followed it; the result stood" },
      { "rule_id": 25, "outcome": "RuleIgnored", "note": "did not apply to this turn" }
    ]

- `UserAccepted` — you followed the rule and the work stood.
- `UserRejected` — you followed it and the user corrected the result.
- `CorrectionRepeated` — the mistake the rule exists to prevent happened anyway.
- `RuleIgnored` — the rule did not apply. The honest, expected answer for most injected
  rules on most turns, and what stops unused rules from drifting upward in confidence.

AgentRecall refuses two kinds of report, so do not send them: an outcome for a rule no
retrieval injected, and `BuildPassed` / `TestsPassed` / `LintPassed` — those must come
from whatever actually ran the command, since asserting that tests passed is not a test
run and would raise a rule's confidence on nothing. Refusals come back in the tool
result; `agentrecall activity last` shows what was recorded.

### AgentRecall behavior contract

When the user asks anything about AgentRecall's state — whether it captured,
saved, ran, or what it did — do not guess, and do not answer based only on
whether you personally called a tool. AgentRecall records every run and every
capture decision, so the answer is always queryable. Check the matching command
and report its actual output:

| User asks | Agent must do |
| --- | --- |
| Did you save anything? | Run `agentrecall capture-status --last-turn` |
| Was anything captured? | Run `agentrecall capture-status --last-turn` |
| Any lesson for AgentRecall? | Run `agentrecall capture-status --last-turn` |
| Did AgentRecall run? | Run `agentrecall activity last` |
| What did AgentRecall do? | Run `agentrecall activity last` |
| What rules were fetched? | Run `agentrecall activity last` |
| Did the Stop hook capture anything? | Run `agentrecall finalize-turn status` |
| Is a judgment still outstanding? | Run `agentrecall capture-status --last-turn` |
| Did you save that as a doc? | Run `agentrecall document status --json` |

Equivalently, call the `capture_status` MCP tool for capture questions. Always
follow this pattern:

1. Check AgentRecall status with the command (or MCP tool) above.
2. Report the actual recorded result — what AgentRecall did, not what you did.
3. Only offer manual capture if the status shows nothing was captured AND the
   user explicitly asks you to save it.

Forbidden answers — never say them:

- "I didn't manually call AgentRecall"
- "The Stop hook may have captured it."
- "I don't control whether it fired"
- "Want me to save it?" — unless the status shows `awaiting_approval: true` (or a
  Suggested/Pending rule) and your approval is genuinely required.

These are wrong because they speculate instead of reading recorded state. Run the
status command and answer from it.

### Answering capture questions: check status, never guess

AgentRecall owns the capture decision; the Stop hook finalizes every turn through
`agentrecall finalize-turn`. So when the user asks whether AgentRecall captured,
saved, added, or remembered anything, you MUST check the finalization status
before answering. Do not answer from memory, and do not reason from whether you
personally called a tool — a manual tool call is not the source of truth.

Call the `capture_status` MCP tool (or run one of the commands below) and answer
from its result — never from memory:

    agentrecall finalize-turn status
    agentrecall capture-status --last-turn

Answer using the recorded decision:

- **Captured, `awaiting_approval: true`** (the default) — "AgentRecall captured rule
  #X: <summary> — awaiting your approval." Relay it and ask yes/no per the section
  above; do not call it settled until the user answers.
- **Captured, `awaiting_approval: false`** — the gate was bypassed (an explicit save,
  or `InteractiveMemoryMode: Silent`) — "AgentRecall captured rule #X: <summary>."
  Already active; nothing to ask.
- **Suggested / Pending** — "AgentRecall suggested pending rule #Y: <summary>." Same
  approval flow as an unapproved capture (`agentrecall rules approve Y`).
- **Skipped** — "AgentRecall skipped capture: <reason>."
- **`awaiting_judgment: true`** — AgentRecall asked for this turn's judgment and is still
  waiting: "AgentRecall is waiting for this turn's capture judgment." Then submit it with
  `submit_capture_judgment` rather than reporting it as a capture outcome.
- **Nothing recorded** — "No finalized AgentRecall capture is recorded for the
  last turn."

Never answer a capture question by speculating. The following are **forbidden
answers — never say them**:

- "The Stop hook may have captured it."
- "I didn't manually call AgentRecall, so nothing was saved."
- "Want me to save it?" — unless the status shows `awaiting_approval: true` (or a
  Suggested/Pending rule) and your approval is genuinely required.

Only call `capture_feedback` yourself when the finalization status shows that no
capture happened AND the user explicitly asks you to save the lesson. Never create
a duplicate of a rule the finalizer already captured — report the existing rule.

On top of that:

- **When the user accepts a review or PR comment** — i.e. asks you to apply or
  fix what a comment says — you may still call `import_pr_comments` with
  `accepted: true` (scope = the repository) to record it explicitly; the capture
  hook also picks up accepted guidance on its own.
- **Before** non-trivial work, call `inject_context` with the task description
  when you need the relevant rules mid-task (the hook already covers prompts).

### Turn Memory Summary

After it finalizes a turn, AgentRecall prints a **Turn Memory Summary** — one
aggregated line (or grouped sections), governed by `AgentRecall.TurnSummaryLevel`
(`Silent` | `Compact` | `Detailed`). It reports the rules AgentRecall used,
captured, suggested, and skipped this turn.

When the user asks "did you save anything?", check
`agentrecall capture-status --last-turn` or `agentrecall turn-summary --last` and
answer from the result. Do not guess, do not mention manual tool calls, and do not
say the hook "may have" captured something. Use the Turn Summary result as the
source of truth.

Good:

- "🧠 AgentRecall captured rule #28: Preserve else semantics when flattening
  nested conditionals."
- "🧠 AgentRecall did not capture a new rule. It skipped 1 candidate because it
  was not reusable enough."

Bad:

- "I didn't manually save anything."
- "The hook may have captured it."

### Interactive Memory

AgentRecall owns the memory decision. Never ask "Want me to save it?" — instead,
report what AgentRecall decided and present its options:

- **AutoCapture** — a high-confidence lesson was stored automatically. Just notify
  the user: "🧠 AgentRecall captured rule #28: <summary>." Do not ask anything.
- **SuggestCapture** — an ambiguous lesson was parked as a Pending rule. Present
  AgentRecall's interactive options, e.g. "🧠 AgentRecall found a possible lesson.
  Reply `remember` to save it or `ignore` to skip." If the user says remember, run
  `agentrecall rules approve <id>`; if ignore, run `agentrecall rules archive <id>`.
- **Skip** — nothing was stored. Do not push the user to save unless they ask.

Forbidden — never say:

- "Want me to save it?"

Say instead, only for SuggestCapture/Pending:

- "🧠 AgentRecall found a possible lesson. Reply `remember` to save it or `ignore`
  to skip."

### Store lessons, not facts

Do not store information that can be recovered from the repository using search,
grep, or code navigation. A method/class/property that exists, a file path, a
config location, one service calling another, or a bare "use method X" is a
**code fact**, not a memory.

Prefer storing:

- recurring mistakes
- review insights
- project conventions
- bug patterns
- cross-layer consistency rules
- engineering decisions

Before saving memory, ask: **"Is this a reusable lesson or merely a code fact?"**

- If it is a code fact, do not save it.
- If it reveals a broader pattern, save the **generalized lesson** instead. For
  example, capture "When implementing feature gates, use the canonical gate
  definition and verify frontend and backend gate conditions remain consistent."
  rather than "Use `IsEventsFeatureEnabled`."

### Store rules as conditional knowledge

AgentRecall stores rules as conditional knowledge. Prefer saving:

- **When** <condition>, **do** <action>
- **Avoid** <anti-pattern>
- **Because** <reason>

Do not save:

- raw code facts
- method existence facts
- file path facts
- implementation details that can be recovered from search

Store **repository conventions** when they reduce repeated agent mistakes, even
if they mention specific methods — e.g. "When implementing Events backend gates,
use `IsEventsFeatureEnabled` instead of `IsVenueMigratedFor`." Store
**engineering lessons** for reusable why/patterns that survive refactors — e.g.
"Frontend and backend feature gate definitions must match."

### User preferences are memory too

When the user explicitly states a durable preference for how you should
communicate — answer length, explanation depth, language, prompt format, how
often to ask questions — AgentRecall may capture it as a **UserPreference**.

- Do not treat a user preference as a repository engineering convention.
- Do not lower its confidence just because it is non-technical; an explicit
  preference is the user's own word, captured with high confidence.
- A communication preference is scoped to the user, not the repository.
- If the user asks whether a preference was saved, check
  `agentrecall capture-status --last-turn` or `agentrecall turn-summary --last`
  and answer from that — do not guess.

Bad:

- "I didn't manually save it."

Good:

- "🧠 AgentRecall captured a user preference: answer briefly and simply first,
  with examples when helpful."

A preference that conflicts with correctness or honesty (e.g. "always agree even
if I'm wrong") is **not** captured.

### Seed rules

AgentRecall may include optional **seed rules** installed from a built-in seed pack
(e.g. `tidy-first`). Once a pack is installed they are **active starter guidance** —
in force from day one — but they are not project-specific truth.

- Apply a seed rule when its When/If condition matches the task.
- Do not treat a seed rule as absolute.
- Prefer project-specific rules over seed rules when they conflict; explicit user
  corrections always override a seed rule.
- A seed rule is marked as seed-derived and starts at moderate confidence; it earns
  more trust from repeated successful use.
- If the user rejects a seed rule for this project (or says it is not applicable),
  AgentRecall lowers its confidence, suppresses it, or archives it — do not keep
  pushing it.

### Career Impact Pack

AgentRecall may include an optional, user-installed **career-impact** seed pack. It
helps detect Staff-level impact, metrics, evidence, ADRs, stakeholders, and
promotion-worthy work. It is opt-in and off unless the user installs it.

- It should not spam the user. A cheap, deterministic detector runs at the end of a
  turn only when the pack is installed and `AgentRecall.CareerImpactMode` is not
  `Silent`; it says nothing for trivial work.
- It should not run full journal generation unless the user asks. The promotion
  journal is produced only on demand via `agentrecall career journal --last`.
- If AgentRecall reports a Career Impact candidate, present the compact summary and
  the command pointer — do not paste a full promotion packet.
- Do not treat career-impact suggestions as project technical truth; they are
  coaching/evidence guidance, not repository facts.
- Do not let career-impact rules override repository conventions or explicit user
  corrections.
