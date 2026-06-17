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

# The named DB volume mounts root-owned; hand it to the container user so the CLI
# and MCP server can write to it.
sudo chown vscode:vscode /home/vscode/.agentrecall

# Restore, then build and install AgentRecall as a global tool from this repo's
# source so the binary matches the checked-out code. `tool update` installs when
# absent and upgrades when present, so it is safe to re-run.
dotnet restore
dotnet pack -c Release -o ./nupkg
dotnet tool update --global --add-source ./nupkg AgentRecall

# Re-register the MCP server with Claude Code. Skipped cleanly if the claude CLI
# isn't installed in the container; tolerated if a registration already exists.
if command -v claude >/dev/null 2>&1; then
  claude mcp add agentrecall agentrecall mcp 2>/dev/null || true
fi

# Confirm the tool resolves on PATH so a botched install surfaces in the create log
# rather than failing silently the first time someone runs `agentrecall`.
export PATH="$PATH:/home/vscode/.dotnet/tools"
echo "AgentRecall ready: $(agentrecall --version)"
