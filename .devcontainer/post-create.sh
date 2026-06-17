#!/usr/bin/env bash
#
# Runs on container create (including "Rebuild Container"). A rebuild discards the
# container filesystem, so two things AgentRecall needs are lost and rebuilt here:
#
#   1. the `agentrecall` global tool       (~/.dotnet/tools)
#   2. its MCP registration with Claude    (~/.claude/.claude.json)
#
# The SQLite database survives separately on the named `agentrecall-data` volume.
set -euo pipefail

TOOLS_DIR="/home/vscode/.dotnet/tools"

# Name each step so that, on failure, the trap can report exactly which command
# aborted the script and make it obvious AgentRecall was not installed — otherwise
# a failed rebuild only surfaces later as a confusing "command not found".
STEP="startup"
log() { echo "==> $1"; STEP="$1"; }
trap 'code=$?;
  echo "" >&2
  echo "ERROR: post-create FAILED during: $STEP (exit $code)." >&2
  echo "ERROR: AgentRecall was NOT installed. Fix the error above and rebuild," >&2
  echo "       or run \"bash .devcontainer/post-create.sh\" to retry." >&2' ERR

# The named DB volume mounts root-owned; hand it to the container user so the CLI
# and MCP server can write to it.
log "fix ~/.agentrecall ownership"
sudo chown vscode:vscode /home/vscode/.agentrecall

# Restore, then build and install AgentRecall as a global tool from this repo's
# source so the binary matches the checked-out code. `tool update` installs when
# absent and upgrades when present, so it is safe to re-run.
log "dotnet restore"
dotnet restore

log "dotnet pack -c Release"
dotnet pack -c Release -o ./nupkg

log "dotnet tool update --global AgentRecall (from ./nupkg)"
dotnet tool update --global --add-source ./nupkg AgentRecall

# Put the global tools directory on PATH for the rest of this script. VS Code starts
# integrated terminals from remoteEnv (set in devcontainer.json), which keeps the
# tool discoverable there.
export PATH="$PATH:$TOOLS_DIR"

# Re-register the MCP server with Claude Code. Skipped cleanly if the claude CLI
# isn't installed in the container; tolerated if a registration already exists.
log "register MCP server with Claude Code"
if command -v claude >/dev/null 2>&1; then
  claude mcp add agentrecall agentrecall mcp 2>/dev/null || true
else
  echo "    (claude CLI not found; skipping MCP registration)"
fi

# Confirm the tool resolves on PATH so a botched install surfaces in the create log
# rather than failing silently the first time someone runs `agentrecall`.
log "verify install"
echo "AgentRecall ready: $(agentrecall --version)"
