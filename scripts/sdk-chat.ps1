# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
#
# SDK Chat Docker Wrapper (Windows PowerShell)
#
# Usage:
#   .\scripts\sdk-chat.ps1 package sample generate C:\path\to\sdk
#   .\scripts\sdk-chat.ps1 doctor
#   .\scripts\sdk-chat.ps1 mcp
#   .\scripts\sdk-chat.ps1 --build package sample generate C:\path\to\sdk  # Build image first
#
# Authentication (choose one):
#   1. GitHub token: $env:GH_TOKEN = "ghp_..." or $env:GITHUB_TOKEN = "ghp_..."
#   2. Copilot credentials: Will mount ~/.copilot if it exists
#   3. OpenAI: $env:OPENAI_API_KEY = "sk-..." and use --use-openai flag
#
# This script handles:
# - Mounting SDK paths as /sdk in the container
# - Passing through Copilot credentials (~/.copilot)
# - Passing through environment variables
# - Building the Docker image with --build flag

param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

$ErrorActionPreference = "Stop"

# Get script and repo directories
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir

$Image = if ($env:SDK_CHAT_IMAGE) { $env:SDK_CHAT_IMAGE } else { "sdk-chat:latest" }

# Check for --build flag
$BuildImage = $false
$FilteredArgs = @()
foreach ($arg in $Arguments) {
    if ($arg -eq "--build") {
        $BuildImage = $true
    } else {
        $FilteredArgs += $arg
    }
}
$Arguments = $FilteredArgs

# Build image if requested
if ($BuildImage) {
    Write-Host "Building Docker image..." -ForegroundColor Cyan
    docker build -f "$RepoRoot\Dockerfile.release" -t $Image $RepoRoot
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Docker build failed"
        exit 1
    }
}

# Check if image exists (auto-build hint)
$ImageExists = docker image inspect $Image 2>$null
if (-not $ImageExists -and -not $BuildImage) {
    Write-Host "Image '$Image' not found. Building automatically..." -ForegroundColor Yellow
    Write-Host "(Use --build to force rebuild)" -ForegroundColor DarkGray
    docker build -f "$RepoRoot\Dockerfile.release" -t $Image $RepoRoot
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Docker build failed"
        exit 1
    }
}

# Build docker run arguments
# Note: Windows Docker doesn't need -u flag as it handles permissions differently
$DockerArgs = @(
    "run"
    "--rm"
)

# Mount Copilot credentials if available (for auth fallback)
# Mount at user's home path for proper credential discovery
$CopilotDir = Join-Path $env:USERPROFILE ".copilot"
if (Test-Path $CopilotDir) {
    $UserHome = $env:USERPROFILE -replace '\\', '/'
    $DockerArgs += @("-v", "${CopilotDir}:${UserHome}/.copilot:ro")
    $DockerArgs += @("-e", "HOME=${UserHome}")
}

# Pass through relevant environment variables if set
$EnvVars = @(
    "OPENAI_API_KEY"
    "OPENAI_ENDPOINT"
    "GH_TOKEN"
    "GITHUB_TOKEN"
    "SDK_CLI_MODEL"
    "SDK_CLI_TIMEOUT"
    "SDK_CLI_DEBUG"
    "SDK_CLI_DEBUG_DIR"
    "SDK_CLI_USE_OPENAI"
    "NO_COLOR"
    "OTEL_EXPORTER_OTLP_ENDPOINT"
    "OTEL_TRACES_EXPORTER"
)

foreach ($var in $EnvVars) {
    $value = [Environment]::GetEnvironmentVariable($var)
    if ($value) {
        $DockerArgs += @("-e", $var)
    }
}

# For interactive commands, add -it flags
$FirstArg = if ($Arguments.Count -gt 0) { $Arguments[0] } else { "" }
if ($FirstArg -eq "acp") {
    $DockerArgs += @("-it")
}

# For MCP stdio, we need stdin and workspace mount
if ($FirstArg -eq "mcp") {
    $ArgsString = $Arguments -join " "
    if ($ArgsString -notmatch "--transport" -or $ArgsString -match "--transport[=\s]+stdio") {
        $DockerArgs += @("-i")
        # Mount workspace if SDK_WORKSPACE is set (from VS Code mcp.json)
        if ($env:SDK_WORKSPACE) {
            $WorkspacePath = $env:SDK_WORKSPACE -replace '\\', '/'
            $DockerArgs += @("-v", "${WorkspacePath}:${WorkspacePath}")
        }
    }

    # For MCP SSE, expose port
    if ($ArgsString -match "--transport[=\s]+sse") {
        $Port = "8080"
        if ($ArgsString -match "--port[=\s]+(\d+)") {
            $Port = $Matches[1]
        }
        $DockerArgs += @("-p", "${Port}:${Port}")
    }
}

# Process arguments to find and mount SDK paths
$ProcessedArgs = @()
foreach ($arg in $Arguments) {
    if (Test-Path -Path $arg -PathType Container) {
        # It's a directory - mount it as /sdk
        $AbsPath = (Resolve-Path $arg).Path
        # Convert Windows path to Docker format
        $DockerPath = $AbsPath -replace '\\', '/'
        $DockerArgs += @("-v", "${DockerPath}:/sdk")
        $ProcessedArgs += "/sdk"
    }
    elseif (Test-Path -Path $arg -PathType Leaf) {
        # It's a file - mount parent directory
        $ParentPath = (Resolve-Path (Split-Path $arg -Parent)).Path
        $FileName = Split-Path $arg -Leaf
        $DockerPath = $ParentPath -replace '\\', '/'
        $DockerArgs += @("-v", "${DockerPath}:/sdk")
        $ProcessedArgs += "/sdk/$FileName"
    }
    else {
        $ProcessedArgs += $arg
    }
}

$DockerArgs += $Image
$DockerArgs += $ProcessedArgs

& docker @DockerArgs
