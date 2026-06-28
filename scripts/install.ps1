#!/usr/bin/env pwsh
#
# Installs (or upgrades) AgentRecall as a .NET global tool and ensures it's on
# PATH so `agentrecall` is runnable immediately, including in new terminals.
# Safe to re-run. Requires the .NET SDK.
$ErrorActionPreference = 'Stop'

# Require the .NET SDK up front with a clear, actionable error.
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "The .NET SDK ('dotnet') was not found on your PATH. Install it from https://dotnet.microsoft.com/download and re-run this script."
    exit 1
}

# `tool update` installs when absent and upgrades when present.
dotnet tool update --global AgentRecall

# Resolve the global tools directory rather than hardcoding it: the .NET CLI honours
# DOTNET_CLI_HOME (falling back to the user profile), with tools under "<base>\.dotnet\tools".
$base = if ($env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME } else { $env:USERPROFILE }
$toolsDir = Join-Path $base '.dotnet/tools'
$agentrecall = Join-Path $toolsDir 'agentrecall.exe'

# Fall back to a PATH lookup if a custom layout put the binary elsewhere.
if (-not (Test-Path $agentrecall)) {
    $command = Get-Command agentrecall -ErrorAction SilentlyContinue
    if ($command) {
        $agentrecall = $command.Source
    }
    else {
        Write-Error "AgentRecall was installed but could not be found in '$toolsDir' or on PATH. Add the .NET global tools directory to your PATH, then run: agentrecall setup"
        exit 1
    }
}

# Ask the freshly-installed tool to put its directory on PATH (idempotent).
& $agentrecall setup
