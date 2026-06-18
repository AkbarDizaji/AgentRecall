#!/usr/bin/env pwsh
#
# Installs (or upgrades) AgentRecall as a .NET global tool and ensures it's on
# PATH so `agentrecall` is runnable immediately, including in new terminals.
# Safe to re-run. Requires the .NET SDK.
$ErrorActionPreference = 'Stop'

# `tool update` installs when absent and upgrades when present.
dotnet tool update --global AgentRecall

# Ask the freshly-installed tool to put its directory on PATH (idempotent). We call
# it by full path because PATH may not include the tools directory yet.
& "$env:USERPROFILE\.dotnet\tools\agentrecall.exe" setup
