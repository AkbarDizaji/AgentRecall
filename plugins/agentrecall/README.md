# AgentRecall (Claude Code plugin)

Local-first memory for AI coding agents. AgentRecall captures feedback and
failures, turns them into ranked, conflict-resolved technical rules, and injects
the relevant ones into Claude Code over MCP. Everything stays on your machine —
no cloud, no API keys.

## Prerequisite (read this first)

This plugin runs the `agentrecall` MCP server, which is a **.NET global tool**.
Unlike `npx`-based servers, it is **not auto-fetched** — you must install it
yourself so the `agentrecall` command is on your `PATH` **before** the plugin's
MCP config will work.

```bash
# Requires the .NET SDK 8 or newer (https://dotnet.microsoft.com/download)
dotnet tool install -g AgentRecall

# Verify the command is on PATH (you may need to open a new terminal, or add
# ~/.dotnet/tools to PATH):
agentrecall --version
```

If `agentrecall` is not found after install, add the .NET tools directory to your
`PATH` (commonly `~/.dotnet/tools`) and restart your terminal / Claude Code.

## Install the plugin

```text
/plugin marketplace add AkbarDizaji/AgentRecall
/plugin install agentrecall@agentrecall-marketplace
```

Restart Claude Code (or reconnect MCP servers) so the `agentrecall` server boots
and its tools register.

## MCP tools exposed

| Tool | What it does |
| --- | --- |
| `inject_context` | Before a task, get the most relevant rules ranked by usefulness. |
| `get_relevant_context` | Before coding/reviewing/refactoring/debugging, fetch the rules that apply. |
| `resolve_rules` | When several rules might apply, resolve them to the ones to follow. |
| `get_project_rules` | Get the rules that always apply for the current project. |
| `get_reminders` | Get a short checklist of reminders for a kind of task (e.g. code review). |
| `search_rules` | Search AgentRecall for technical coding rules relevant to a query. |
| `capture_feedback` | Save a correction in one step: records the event and generates a rule. |
| `add_feedback` | Record corrective feedback about an agent's work. |
| `import_pr_comments` | Capture pull-request review comments as feedback. |
| `suggest_feedback_candidate` | Check whether a user message is a reusable coding correction. |
| `compress_memory` | Reduce memory duplication by merging duplicate/overlapping rules. |

## Links

- Project & full documentation: https://github.com/AkbarDizaji/AgentRecall
- NuGet package: https://www.nuget.org/packages/AgentRecall

License: MIT.
