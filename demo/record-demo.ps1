#!/usr/bin/env pwsh
# Record demo GIF using the project's dev container
# Works on Windows, macOS, and Linux with PowerShell Core
# Usage: pwsh ./record-demo.ps1

$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

# Get GH_TOKEN from gh CLI or environment
$GH_TOKEN = $env:GH_TOKEN
if (-not $GH_TOKEN) {
    try {
        $GH_TOKEN = & gh auth token 2>$null
    } catch {
        # Fallback: try to read from gh config
        $configPath = if ($IsWindows) { 
            "$env:APPDATA\GitHub CLI\hosts.yml" 
        } else { 
            "$HOME/.config/gh/hosts.yml" 
        }
        if (Test-Path $configPath) {
            $content = Get-Content $configPath -Raw
            if ($content -match "oauth_token:\s*(\S+)") {
                $GH_TOKEN = $Matches[1]
            }
        }
    }
}

if (-not $GH_TOKEN) {
    Write-Error "GH_TOKEN not found. Run 'gh auth login' first or set GH_TOKEN environment variable."
    exit 1
}

Write-Host "Building container..." -ForegroundColor Cyan
docker build -t sdk-chat-dev .

Write-Host "Recording demo..." -ForegroundColor Cyan
docker run --rm -v "${PWD}:/workspace" -e "GH_TOKEN=$GH_TOKEN" --entrypoint /workspace/demo/entrypoint.sh sdk-chat-dev

Write-Host "Done!" -ForegroundColor Green
Get-Item demo/demo.gif | Select-Object Name, Length, LastWriteTime
