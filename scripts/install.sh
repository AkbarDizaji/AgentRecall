#!/usr/bin/env bash
#
# Installs (or upgrades) AgentRecall as a .NET global tool and ensures it's on
# PATH so `agentrecall` is runnable immediately, including in new shells.
# Safe to re-run. Requires the .NET SDK.
set -euo pipefail

# Require the .NET SDK up front with a clear, actionable error.
if ! command -v dotnet >/dev/null 2>&1; then
  echo "error: the .NET SDK ('dotnet') was not found on your PATH." >&2
  echo "       Install it from https://dotnet.microsoft.com/download and re-run this script." >&2
  exit 1
fi

# `tool update` installs when absent and upgrades when present.
dotnet tool update --global AgentRecall

# Resolve the global tools directory rather than hardcoding it: the .NET CLI honours
# DOTNET_CLI_HOME (falling back to HOME), and the tools live under "<base>/.dotnet/tools".
tools_dir="${DOTNET_CLI_HOME:-$HOME}/.dotnet/tools"
agentrecall="$tools_dir/agentrecall"

# Fall back to a PATH lookup if a custom layout put the binary elsewhere.
if [ ! -x "$agentrecall" ]; then
  if command -v agentrecall >/dev/null 2>&1; then
    agentrecall="$(command -v agentrecall)"
  else
    echo "error: AgentRecall was installed but could not be found in '$tools_dir' or on PATH." >&2
    echo "       Add the .NET global tools directory to your PATH, then run: agentrecall setup" >&2
    exit 1
  fi
fi

# Ask the freshly-installed tool to put its directory on PATH (idempotent).
"$agentrecall" setup
