using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentRecall.Cli.Devcontainer;

/// <summary>
/// Scaffolds the dev container wiring that reinstalls AgentRecall on every
/// "Rebuild Container". A rebuild discards the container filesystem, so the
/// globally-installed tool and its Claude Code MCP registration are lost; the
/// generated <c>postCreateCommand</c> script rebuilds them while the SQLite
/// database survives on a named Docker volume.
/// </summary>
public static class DevcontainerScaffolder
{
    /// <summary>Named Docker volume that backs the persisted database directory.</summary>
    public const string DataVolumeName = "agentrecall-data";

    /// <summary>Container path the data volume mounts at, and that the CLI is pinned to.</summary>
    public const string DataDirectory = "/home/vscode/.agentrecall";

    /// <summary>Path, relative to the project root, of the generated setup script.</summary>
    public const string PostCreateRelativePath = ".devcontainer/agentrecall-post-create.sh";

    /// <summary>Path, relative to the project root, of the dev container manifest.</summary>
    public const string DevcontainerJsonRelativePath = ".devcontainer/devcontainer.json";

    /// <summary>Path, relative to the project root, of the Claude Code project settings.</summary>
    public const string ClaudeSettingsRelativePath = ".claude/settings.json";

    /// <summary>
    /// Prefix that puts the .NET global-tools directory on PATH for the hook's shell.
    /// Claude Code may be launched from a GUI/IDE whose environment doesn't include
    /// <c>~/.dotnet/tools</c>, and it runs hooks via a non-login <c>/bin/sh</c> — so a
    /// bare <c>agentrecall</c> resolves to "command not found". Using <c>$HOME</c> (and
    /// no quotes — there are no spaces in the value) keeps the command portable across
    /// machines and identical on host and dev container, and JSON-safe in settings.json.
    /// </summary>
    public const string HookPathPrefix = "PATH=$HOME/.dotnet/tools:$PATH ";

    /// <summary>The invariant tail that identifies the recall hook (ignoring any PATH prefix).</summary>
    public const string RecallHookMarker = "agentrecall hook user-prompt-submit";

    /// <summary>
    /// The command Claude Code runs on each prompt so AgentRecall rules are injected
    /// deterministically (rather than left to the model to call an MCP tool).
    /// </summary>
    public const string HookCommand = HookPathPrefix + RecallHookMarker;

    /// <summary>The Claude Code event the recall hook binds to.</summary>
    public const string RecallHookEvent = "UserPromptSubmit";

    /// <summary>
    /// The legacy capture-hook tail. Older projects registered this under <c>Stop</c>;
    /// re-running init upgrades it in place to the turn finalizer below.
    /// </summary>
    public const string CaptureHookMarker = "agentrecall hook capture";

    /// <summary>The legacy capture-hook command (superseded by the turn finalizer).</summary>
    public const string CaptureHookCommand = HookPathPrefix + CaptureHookMarker;

    /// <summary>The invariant tail that identifies the turn-finalizer hook (ignoring any PATH prefix).</summary>
    public const string FinalizeTurnMarker = "agentrecall finalize-turn";

    /// <summary>
    /// The command Claude Code runs after a turn so AgentRecall finalizes it
    /// deterministically — extracting reusable lessons and deciding to auto-capture,
    /// suggest, or skip (rather than left to the model to call an MCP tool). The
    /// <c>--hook</c> flag makes it emit a non-blocking <c>systemMessage</c> notice.
    /// </summary>
    public const string FinalizeTurnHookCommand = HookPathPrefix + FinalizeTurnMarker + " --hook";

    /// <summary>
    /// The Claude Code event the finalizer hook binds to. <c>Stop</c> fires after the
    /// assistant finishes a turn — the post-response hook AgentRecall finalizes from.
    /// </summary>
    public const string CaptureHookEvent = "Stop";

    /// <summary>Path, relative to the project root, of the agent guidance file.</summary>
    public const string ClaudeMdRelativePath = "CLAUDE.md";

    /// <summary>Heading that marks the AgentRecall guidance block (used for idempotency).</summary>
    public const string ClaudeMdHeading = "## Memory (AgentRecall)";

    /// <summary>
    /// Standing guidance appended to <c>CLAUDE.md</c> so the agent recalls rules and
    /// captures accepted review comments by default — the "active" behaviour the hook
    /// alone can't express (the hook injects context; capture is still a tool call).
    /// </summary>
    public static string ClaudeMdGuidance =>
        $$"""
        {{ClaudeMdHeading}}

        The `agentrecall` MCP server holds rules learned from past feedback. Recall and
        capture are both wired as deterministic hooks: the UserPromptSubmit hook injects
        the relevant rules automatically, and the Stop hook finalizes each turn through
        `agentrecall finalize-turn`, which extracts reusable lessons and decides — on its
        own — whether to auto-capture, suggest, or skip each one (no tool call required).

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

        """;

    /// <summary>
    /// The self-contained setup script. Pins the data directory onto the persisted
    /// volume and self-heals AgentRecall: when the binary is missing (common after a
    /// rebuild) it reinstalls before touching the MCP registration, and only removes a
    /// stale registration if the reinstall fails — without ever blocking startup.
    /// Idempotent, so it is safe to re-run on every create/rebuild.
    /// </summary>
    public static string PostCreateScript =>
        """
        #!/usr/bin/env bash
        #
        # Installs AgentRecall as a global .NET tool and registers its MCP server with
        # Claude Code. Generated by `agentrecall devcontainer init`.
        #
        # "Rebuild Container" discards the container filesystem, so the globally-installed
        # tool (~/.dotnet/tools) and the MCP registration (~/.claude/.claude.json) are
        # rebuilt here on every create. The SQLite database survives separately on the
        # named `agentrecall-data` Docker volume.
        set -euo pipefail

        TOOLS_DIR="$HOME/.dotnet/tools"

        # Where AgentRecall stores its database. Matches the devcontainer.json mount and
        # env; falls back to the per-user default when that env var isn't set.
        DATA_DIR="${AGENTRECALL_AgentRecall__DataDirectory:-$HOME/.agentrecall}"

        # Name each step and, on failure, say which command aborted the script and that
        # AgentRecall was not installed — so a rebuild failure is easy to diagnose
        # instead of surfacing later as a confusing "command not found".
        STEP="startup"
        log()  { echo "==> $1"; STEP="$1"; }
        trap 'code=$?; echo "" >&2; echo "AgentRecall: setup FAILED during: $STEP (exit $code)." >&2; echo "AgentRecall: the tool was NOT installed; fix the error above and rebuild, or" >&2; echo "             run \"bash .devcontainer/agentrecall-post-create.sh\" to retry." >&2' ERR

        # A named Docker volume mounts root-owned on first create, so a non-root container
        # user can't write to it. Make it writable using whatever the image actually
        # offers (we may already be root, or have sudo, or neither) — best effort, never
        # fatal, and never assuming `sudo` exists.
        log "ensure data directory is writable"
        ensure_writable() {
          mkdir -p "$DATA_DIR" 2>/dev/null || true
          [ -w "$DATA_DIR" ] && return 0
          if [ "$(id -u)" = "0" ]; then
            chown -R "$(id -u):$(id -g)" "$DATA_DIR" 2>/dev/null || true
          elif command -v sudo >/dev/null 2>&1; then
            sudo chown -R "$(id -u):$(id -g)" "$DATA_DIR" 2>/dev/null || true
          fi
          if [ ! -w "$DATA_DIR" ]; then
            echo "AgentRecall: $DATA_DIR is not writable by $(id -un), and this image" \
                 "offers no way to fix it (not root, no sudo)." >&2
            echo "AgentRecall: add sudo to the image, set \"remoteUser\": \"root\", or" \
                 "chown the volume in your Dockerfile, then rebuild." >&2
          fi
        }
        ensure_writable

        # Make ~/.dotnet/tools discoverable. VS Code often starts bash as a NON-login
        # shell, so ~/.profile isn't sourced and a global tool reads as "not found" even
        # when installed. Put it on PATH for this shell and persist to ~/.bashrc
        # (idempotent), and print the exact remoteEnv snippet.
        log "ensure $TOOLS_DIR is on PATH"
        case ":$PATH:" in
          *":$TOOLS_DIR:"*)
            : # already on PATH for this shell
            ;;
          *)
            if ! { [ -f "$HOME/.bashrc" ] && grep -qF "$TOOLS_DIR" "$HOME/.bashrc"; }; then
              printf '\n# Added by AgentRecall: put .NET global tools on PATH for non-login shells\nexport PATH="$PATH:%s"\n' "$TOOLS_DIR" >> "$HOME/.bashrc" 2>/dev/null \
                && echo "AgentRecall: appended a PATH export to ~/.bashrc for new terminals." >&2
            fi
            echo "AgentRecall: to set PATH for VS Code permanently, add to .devcontainer/devcontainer.json:" >&2
            echo "             \"remoteEnv\": { \"PATH\": \"\${containerEnv:PATH}:$TOOLS_DIR\" }" >&2
            ;;
        esac
        export PATH="$PATH:$TOOLS_DIR"

        # Resolve the agentrecall binary: on PATH, or freshly installed under TOOLS_DIR.
        find_agentrecall() {
          command -v agentrecall 2>/dev/null && return 0
          [ -x "$TOOLS_DIR/agentrecall" ] && { printf '%s\n' "$TOOLS_DIR/agentrecall"; return 0; }
          return 1
        }

        # Self-healing install. A rebuild wipes ~/.dotnet/tools while the MCP registration
        # can persist on a volume. If the binary is missing we try to REPAIR it before
        # touching the registration; only if repair fails do we remove the stale entry.
        # The reinstall is guarded by `if`, so a failure never trips `set -e`/the ERR trap
        # and never blocks container startup — we fall through to cleanup instead.
        log "ensure AgentRecall is installed (self-heal if missing)"
        AGENTRECALL_BIN="$(find_agentrecall || true)"
        if [ -n "$AGENTRECALL_BIN" ]; then
          echo "    (agentrecall present at $AGENTRECALL_BIN; no reinstall needed)"
        else
          echo "AgentRecall: binary missing; attempting reinstall: dotnet tool update --global AgentRecall" >&2
          if dotnet tool update --global AgentRecall; then
            AGENTRECALL_BIN="$(find_agentrecall || true)"
          else
            echo "AgentRecall: reinstall command exited non-zero ($?)." >&2
          fi
        fi

        if [ -n "$AGENTRECALL_BIN" ]; then
          # Repaired (or never broken). Initialize the DB and (re-)register the MCP server
          # by ABSOLUTE path so Claude Code starts it regardless of its spawn PATH.
          log "initialize the database"
          "$AGENTRECALL_BIN" init || echo "AgentRecall: 'agentrecall init' failed; see the warning above." >&2

          log "register the MCP server with Claude Code"
          if command -v claude >/dev/null 2>&1; then
            claude mcp remove agentrecall >/dev/null 2>&1 || true
            claude mcp add agentrecall "$AGENTRECALL_BIN" mcp 2>/dev/null \
              && echo "AgentRecall: MCP server registered ($AGENTRECALL_BIN)." \
              || echo "AgentRecall: 'claude mcp add' failed; register manually: claude mcp add agentrecall \"$AGENTRECALL_BIN\" mcp" >&2
          else
            echo "    (claude CLI not found; skipping MCP registration)"
          fi

          echo "AgentRecall ready: $("$AGENTRECALL_BIN" --version 2>/dev/null || echo 'unknown')"
        else
          # Repair failed. Remove the stale registration so Claude Code does not try to
          # start a server whose binary is missing, explain why, and continue (non-fatal).
          log "remove stale MCP registration (repair failed)"
          if command -v claude >/dev/null 2>&1; then
            claude mcp remove agentrecall >/dev/null 2>&1 || true
          fi
          echo "AgentRecall: reinstall failed and the binary is still missing." >&2
          echo "             Removed any stale MCP registration so Claude Code won't start a" >&2
          echo "             missing server. Fix the install with:" >&2
          echo "               dotnet tool update --global AgentRecall" >&2
          echo "             then rerun: bash .devcontainer/agentrecall-post-create.sh" >&2
          echo "AgentRecall: not operational (binary missing); see the warnings above."
        fi

        """;

    /// <summary>
    /// A complete <c>devcontainer.json</c> generated only when the project has none.
    /// </summary>
    public static string DevcontainerJson =>
        """
        {
          // Dev container with AgentRecall preinstalled. Generated by
          // `agentrecall devcontainer init`.
          "name": "AgentRecall-enabled",
          "image": "mcr.microsoft.com/devcontainers/base:ubuntu",

          "features": {
            // AgentRecall is a .NET 10 global tool.
            "ghcr.io/devcontainers/features/dotnet:2": { "version": "10.0" }
          },

          // Persist the AgentRecall SQLite database across container rebuilds. The DB
          // lives under ~/.agentrecall, which is on the (rebuild-wiped) container
          // filesystem; backing it with a named Docker volume keeps the data until the
          // volume is explicitly removed (`docker volume rm agentrecall-data`).
          "mounts": [
            "source=agentrecall-data,target=/home/vscode/.agentrecall,type=volume"
          ],

          // Pin the data directory to the mounted path so the DB always lands on the
          // volume, regardless of which user/process runs the CLI or MCP server.
          "containerEnv": {
            "AGENTRECALL_AgentRecall__DataDirectory": "/home/vscode/.agentrecall"
          },

          "remoteEnv": {
            // So a globally-installed `agentrecall` tool is on PATH.
            "PATH": "${containerEnv:PATH}:/home/vscode/.dotnet/tools"
          },

          // Reinstalls AgentRecall and re-registers its MCP server on every rebuild.
          "postCreateCommand": "bash .devcontainer/agentrecall-post-create.sh"
        }

        """;

    /// <summary>
    /// Generates the setup script and, when the project has no
    /// <c>devcontainer.json</c>, a complete manifest. Returns a description of what
    /// was written and any manual steps the caller must apply to an existing manifest.
    /// </summary>
    /// <param name="projectRoot">Root directory whose <c>.devcontainer</c> is targeted.</param>
    public static DevcontainerInitResult Init(string projectRoot)
    {
        var devcontainerDir = Path.Combine(projectRoot, ".devcontainer");
        Directory.CreateDirectory(devcontainerDir);

        var scriptPath = Path.Combine(projectRoot, PostCreateRelativePath);
        var scriptExisted = File.Exists(scriptPath);
        File.WriteAllText(scriptPath, PostCreateScript);
        MakeExecutable(scriptPath);

        // Wire the deterministic hooks into the project's Claude Code settings: the
        // UserPromptSubmit hook injects recall context, and the Stop hook captures
        // reusable lessons after each turn. The hooks live in the workspace (persist
        // across rebuilds and are committed), so unlike the MCP registration they are
        // set once here, not per-rebuild.
        var hookOutcome = EnsureUserPromptSubmitHook(projectRoot);
        var captureHookOutcome = EnsureCaptureHook(projectRoot);

        // Append standing guidance so the agent recalls rules and captures accepted
        // review comments by default.
        var guidanceOutcome = EnsureClaudeMdGuidance(projectRoot);

        var jsonPath = Path.Combine(projectRoot, DevcontainerJsonRelativePath);
        var jsonExisted = File.Exists(jsonPath);
        if (!jsonExisted)
        {
            File.WriteAllText(jsonPath, DevcontainerJson);
        }

        return new DevcontainerInitResult(
            ScriptPath: PostCreateRelativePath,
            ScriptOverwritten: scriptExisted,
            CreatedDevcontainerJson: !jsonExisted,
            DevcontainerJsonPath: DevcontainerJsonRelativePath,
            ManualSteps: jsonExisted ? ExistingManifestInstructions : null,
            HookOutcome: hookOutcome,
            CaptureHookOutcome: captureHookOutcome,
            ClaudeSettingsPath: ClaudeSettingsRelativePath,
            GuidanceOutcome: guidanceOutcome,
            ClaudeMdPath: ClaudeMdRelativePath);
    }

    /// <summary>
    /// Ensures <c>CLAUDE.md</c> contains the current AgentRecall guidance block. Appends
    /// it when absent, and — crucially — refreshes an older block <em>in place</em> when
    /// its content has drifted from the current guidance, so re-running init upgrades the
    /// behaviour contract without ever duplicating the block. Idempotent: an up-to-date
    /// block (and the rest of the file) is left untouched.
    /// </summary>
    public static GuidanceOutcome EnsureClaudeMdGuidance(string projectRoot)
    {
        var path = Path.Combine(projectRoot, ClaudeMdRelativePath);

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            var start = existing.IndexOf(ClaudeMdHeading, StringComparison.Ordinal);
            if (start >= 0)
            {
                // The block runs from its heading to the next top-level (## ) heading or
                // end of file. The guidance itself uses only ### subheadings, so the next
                // "## " marks where the user's own content resumes.
                var end = NextTopLevelHeadingIndex(existing, start + ClaudeMdHeading.Length);

                var before = existing[..start];
                var after = existing[end..];
                var currentBlock = existing[start..end];

                // Already up to date: leave the whole file byte-for-byte unchanged.
                if (currentBlock.TrimEnd() == ClaudeMdGuidance.TrimEnd())
                {
                    return GuidanceOutcome.AlreadyPresent;
                }

                // Refresh the block in place, preserving everything around it. The
                // guidance ends with a newline, so the following content stays separated.
                var rebuilt = before + ClaudeMdGuidance.TrimEnd() + "\n" + after;
                File.WriteAllText(path, rebuilt);
                return GuidanceOutcome.Updated;
            }

            // Separate from prior content with a blank line, without rewriting it.
            var separator = existing.EndsWith('\n') ? "\n" : "\n\n";
            File.AppendAllText(path, separator + ClaudeMdGuidance);
            return GuidanceOutcome.Appended;
        }

        File.WriteAllText(path, ClaudeMdGuidance);
        return GuidanceOutcome.Created;
    }

    /// <summary>
    /// Index of the next top-level Markdown heading (a line beginning with <c>## </c>)
    /// at or after <paramref name="from"/>, or the string length when there is none.
    /// </summary>
    private static int NextTopLevelHeadingIndex(string text, int from)
    {
        // IndexOf("\n## ") matches a level-2 heading only: "\n### " differs at the third
        // character, so deeper subheadings inside the guidance are correctly skipped.
        var idx = text.IndexOf("\n## ", from, StringComparison.Ordinal);
        return idx < 0 ? text.Length : idx + 1; // start of the "## …" line
    }

    /// <summary>
    /// Ensures the project's <c>.claude/settings.json</c> registers the AgentRecall
    /// recall (<c>UserPromptSubmit</c>) hook, creating or merging without disturbing
    /// other settings. Idempotent: a second run reports the hook is already present.
    /// </summary>
    public static HookSetupOutcome EnsureUserPromptSubmitHook(string projectRoot) =>
        EnsureHook(projectRoot, RecallHookEvent, HookCommand, RecallHookMarker);

    /// <summary>
    /// Ensures the project's <c>.claude/settings.json</c> registers the AgentRecall
    /// turn-finalizer (<c>Stop</c>) hook so reusable lessons are captured
    /// deterministically after each turn. Creating or merging without disturbing other
    /// settings, idempotent like the recall hook, and self-healing: an older
    /// <c>agentrecall hook capture</c> registration is upgraded in place to the finalizer.
    /// </summary>
    public static HookSetupOutcome EnsureCaptureHook(string projectRoot) =>
        EnsureHook(projectRoot, CaptureHookEvent, FinalizeTurnHookCommand, FinalizeTurnMarker, CaptureHookMarker);

    /// <summary>
    /// Registers <paramref name="command"/> under the given Claude Code
    /// <paramref name="eventName"/> in <c>.claude/settings.json</c>, merge-safe and
    /// idempotent: the command appears at most once per event. When a prior AgentRecall
    /// registration for the same hook is found by <paramref name="marker"/> (e.g. an
    /// older bare <c>agentrecall hook …</c> that the host shell can't resolve), its
    /// command is upgraded in place rather than appended — so re-running init heals it.
    /// Any of <paramref name="legacyMarkers"/> also identifies a prior registration to
    /// upgrade, so a hook whose command changed across versions is replaced, not duplicated.
    /// </summary>
    private static HookSetupOutcome EnsureHook(
        string projectRoot,
        string eventName,
        string command,
        string marker,
        params string[] legacyMarkers)
    {
        var path = Path.Combine(projectRoot, ClaudeSettingsRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var existed = File.Exists(path);
        JsonObject root;
        if (existed)
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                root = new JsonObject();
            }
            else
            {
                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(
                        text,
                        documentOptions: new JsonDocumentOptions
                        {
                            CommentHandling = JsonCommentHandling.Skip,
                            AllowTrailingCommas = true,
                        });
                }
                catch (JsonException)
                {
                    // A malformed settings file is the user's to fix; never clobber it.
                    return HookSetupOutcome.SettingsUnparseable;
                }

                if (parsed is not JsonObject obj)
                {
                    return HookSetupOutcome.SettingsUnparseable;
                }

                root = obj;
            }
        }
        else
        {
            root = new JsonObject();
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        if (hooks[eventName] is not JsonArray matchers)
        {
            matchers = new JsonArray();
            hooks[eventName] = matchers;
        }

        // If AgentRecall already registered this hook in any form, upgrade it in place
        // (e.g. an older PATH-less command) instead of appending a second, duplicate
        // matcher — which would leave the broken command still firing alongside the fix.
        var markers = new[] { marker }.Concat(legacyMarkers).ToArray();
        var existingCommand = FindHookCommand(matchers, markers);
        if (existingCommand is not null)
        {
            if (existingCommand["command"]?.GetValue<string>() == command)
            {
                return HookSetupOutcome.AlreadyPresent;
            }

            existingCommand["command"] = command;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return HookSetupOutcome.Merged;
        }

        matchers.Add(new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                },
            },
        });

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return existed ? HookSetupOutcome.Merged : HookSetupOutcome.Created;
    }

    /// <summary>
    /// Returns the inner <c>{ "type": "command", "command": … }</c> object for the first
    /// hook whose command contains any of <paramref name="markers"/> (i.e. an AgentRecall
    /// hook, current or legacy, with or without a PATH prefix), or null when none is
    /// registered.
    /// </summary>
    private static JsonObject? FindHookCommand(JsonArray matchers, string[] markers)
    {
        foreach (var matcher in matchers)
        {
            if (matcher?["hooks"] is not JsonArray inner)
            {
                continue;
            }

            foreach (var entry in inner)
            {
                if (entry is JsonObject obj
                    && obj["command"]?.GetValue<string>() is { } cmd
                    && markers.Any(m => cmd.Contains(m, StringComparison.Ordinal)))
                {
                    return obj;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The keys a user must merge into an existing <c>devcontainer.json</c>, since
    /// AgentRecall does not rewrite a hand-maintained (JSONC) manifest.
    /// </summary>
    public static string ExistingManifestInstructions =>
        """
        Add (or merge) these keys into your existing .devcontainer/devcontainer.json:

          "features": {
            "ghcr.io/devcontainers/features/dotnet:2": { "version": "10.0" }
          },
          "mounts": [
            "source=agentrecall-data,target=/home/vscode/.agentrecall,type=volume"
          ],
          "containerEnv": {
            "AGENTRECALL_AgentRecall__DataDirectory": "/home/vscode/.agentrecall"
          },
          "remoteEnv": {
            "PATH": "${containerEnv:PATH}:/home/vscode/.dotnet/tools"
          },
          "postCreateCommand": "bash .devcontainer/agentrecall-post-create.sh"

        If you already use postCreateCommand, chain the script instead of replacing it, e.g.:
          "postCreateCommand": "<your existing command> && bash .devcontainer/agentrecall-post-create.sh"
        """;

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // chmod 0755 so the dev container can invoke the script directly.
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}

/// <summary>How the UserPromptSubmit hook was wired into Claude Code settings.</summary>
public enum HookSetupOutcome
{
    /// <summary>Settings file created with the hook.</summary>
    Created,

    /// <summary>Hook merged into an existing settings file.</summary>
    Merged,

    /// <summary>The hook was already registered; nothing changed.</summary>
    AlreadyPresent,

    /// <summary>The existing settings file is not valid JSON and was left untouched.</summary>
    SettingsUnparseable,
}

/// <summary>How the AgentRecall guidance block was applied to CLAUDE.md.</summary>
public enum GuidanceOutcome
{
    /// <summary>CLAUDE.md created with the guidance block.</summary>
    Created,

    /// <summary>Guidance appended to an existing CLAUDE.md.</summary>
    Appended,

    /// <summary>An older guidance block was refreshed in place (no duplication).</summary>
    Updated,

    /// <summary>The guidance block was already present and current; nothing changed.</summary>
    AlreadyPresent,
}

/// <summary>Outcome of <see cref="DevcontainerScaffolder.Init"/>.</summary>
/// <param name="ScriptPath">Relative path of the generated setup script.</param>
/// <param name="ScriptOverwritten">Whether an existing script was overwritten.</param>
/// <param name="CreatedDevcontainerJson">Whether a fresh manifest was written.</param>
/// <param name="DevcontainerJsonPath">Relative path of the manifest.</param>
/// <param name="ManualSteps">Steps to apply to an existing manifest, or null.</param>
/// <param name="HookOutcome">How the UserPromptSubmit (recall) hook was wired in.</param>
/// <param name="CaptureHookOutcome">How the Stop (capture) hook was wired in.</param>
/// <param name="ClaudeSettingsPath">Relative path of the Claude Code settings file.</param>
/// <param name="GuidanceOutcome">How the CLAUDE.md guidance block was applied.</param>
/// <param name="ClaudeMdPath">Relative path of the guidance file.</param>
public sealed record DevcontainerInitResult(
    string ScriptPath,
    bool ScriptOverwritten,
    bool CreatedDevcontainerJson,
    string DevcontainerJsonPath,
    string? ManualSteps,
    HookSetupOutcome HookOutcome,
    HookSetupOutcome CaptureHookOutcome,
    string ClaudeSettingsPath,
    GuidanceOutcome GuidanceOutcome,
    string ClaudeMdPath);
