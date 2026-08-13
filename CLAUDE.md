## Memory (AgentRecall)

The `agentrecall` MCP server holds rules learned from past feedback. Recall and
capture are both wired as deterministic hooks: the UserPromptSubmit hook injects
the relevant rules automatically, and the Stop hook finalizes each turn through
`agentrecall finalize-turn`. AgentRecall's memory decisions come from a **semantic
capture judge**, not from keyword heuristics: the judge decides whether the turn
holds memory-worthy content, and AgentRecall only validates that decision and
persists it.

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
        "capture_reason": "ExplicitUserSave | ExplicitUserDoNotSave | ObservedAgentFailure | ReviewerCorrection | UserCorrection | RepositoryConvention | UserPreference | RepeatedMistake | DocBackedCorrection | DuplicateExisting | AssistantProse | SourceDocumentOnly | CommandOutputOnly | LogOutputOnly | CodeFact | NotReusable | Ambiguous | NotMemory",
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

Enforcement is configurable via `AgentRecall.JudgmentEnforcementMode`: `Substantive`
(default), `Always`, or `Off` (never block; turns finalize unjudged as before).

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
- "Want me to save it?" — unless the status says SuggestCapture/Pending and your
  approval is genuinely required.

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

- **Captured** — "AgentRecall captured rule #X: <summary>."
- **Suggested / Pending** — "AgentRecall suggested pending rule #Y: <summary>."
  Only in this case may you ask the user to confirm it (`agentrecall rules
  approve Y`).
- **Skipped** — "AgentRecall skipped capture: <reason>."
- **`awaiting_judgment: true`** — AgentRecall asked for this turn's judgment and is
  still waiting: "AgentRecall is waiting for this turn's capture judgment." Submit it
  with `submit_capture_judgment` instead of reporting a capture outcome.
- **Nothing recorded** — "No finalized AgentRecall capture is recorded for the
  last turn."

Never answer a capture question by speculating. The following are **forbidden
answers — never say them**:

- "The Stop hook may have captured it."
- "I didn't manually call AgentRecall, so nothing was saved."
- "Want me to save it?" — unless the status says SuggestCapture/Pending and your
  approval is genuinely required.

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
