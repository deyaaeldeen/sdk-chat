#!/usr/bin/env bash
# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
#
# SDK Chat Docker Wrapper (Linux/macOS)
#
# Usage:
#   ./scripts/sdk-chat.sh package sample generate /path/to/sdk
#   ./scripts/sdk-chat.sh doctor
#   ./scripts/sdk-chat.sh mcp
#   ./scripts/sdk-chat.sh --build package sample generate /path/to/sdk
#
# Options:
#   --build    Build the Docker image before running (uses Dockerfile.release)
#
# Authentication (choose one):
#   1. GitHub token: export GH_TOKEN="ghp_..." or GITHUB_TOKEN="ghp_..."
#   2. Copilot credentials: Will mount ~/.copilot if it exists
#   3. OpenAI: export OPENAI_API_KEY="sk-..." and use --use-openai flag
#
# This script handles:
# - Mounting SDK paths as /sdk in the container
# - Passing through Copilot credentials (~/.copilot)
# - Passing through environment variables

set -euo pipefail

IMAGE="${SDK_CHAT_IMAGE:-sdk-chat:latest}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# Check for --build flag
BUILD_IMAGE=false
ARGS=("$@")
if [[ "${1:-}" == "--build" ]]; then
    BUILD_IMAGE=true
    ARGS=("${@:2}")  # Remove --build from args
fi

# Build image if requested or if it doesn't exist
if [[ "$BUILD_IMAGE" == "true" ]]; then
    echo "Building Docker image: ${IMAGE}..."
    docker build \
        --build-arg USER_ID="$(id -u)" \
        --build-arg GROUP_ID="$(id -g)" \
        -f "${REPO_ROOT}/Dockerfile.release" -t "${IMAGE}" "${REPO_ROOT}"
    echo ""
elif ! docker image inspect "${IMAGE}" &>/dev/null; then
    echo "Docker image '${IMAGE}' not found."
    echo "Build it with: docker build -f Dockerfile.release -t ${IMAGE} ."
    echo "Or run with --build flag: $0 --build ${*}"
    exit 1
fi

# Build docker run arguments
DOCKER_ARGS=(
    --rm
    -u "$(id -u):$(id -g)"
)

# Mount Copilot credentials if available (for auth fallback)
# Note: NOT read-only because copilot CLI needs to extract its bundled package
# on first run to ~/.copilot/pkg/
if [[ -d "${HOME}/.copilot" ]]; then
    DOCKER_ARGS+=(-v "${HOME}/.copilot:${HOME}/.copilot")
    DOCKER_ARGS+=(-e "HOME=${HOME}")
fi

# Pass through relevant environment variables if set
declare -a ENV_VARS=(
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

for var in "${ENV_VARS[@]}"; do
    if [[ -n "${!var:-}" ]]; then
        DOCKER_ARGS+=(-e "${var}")
    fi
done

# For interactive commands (like acp), add -it flags
if [[ "${ARGS[0]:-}" == "acp" ]] || [[ -t 0 && -t 1 ]]; then
    DOCKER_ARGS+=(-it)
fi

# For MCP stdio, we need stdin and workspace mount (paths come via JSON, not args)
if [[ "${ARGS[0]:-}" == "mcp" ]] && [[ "${ARGS[1]:-}" != "--transport" || "${ARGS[2]:-}" == "stdio" ]]; then
    DOCKER_ARGS+=(-i)
    # Mount workspace if SDK_WORKSPACE is set (from VS Code mcp.json)
    if [[ -n "${SDK_WORKSPACE:-}" ]]; then
        DOCKER_ARGS+=(-v "${SDK_WORKSPACE}:${SDK_WORKSPACE}")
    fi
fi

# For MCP SSE, expose port
ARGS_STR="${ARGS[*]:-}"
if [[ "${ARGS[0]:-}" == "mcp" ]] && [[ "${ARGS_STR}" == *"--transport sse"* || "${ARGS_STR}" == *"--transport=sse"* ]]; then
    # Extract port from args, default to 8080
    PORT=8080
    if [[ "${ARGS_STR}" =~ --port[=\ ]([0-9]+) ]]; then
        PORT="${BASH_REMATCH[1]}"
    fi
    DOCKER_ARGS+=(-p "${PORT}:${PORT}")
fi

# Process arguments to find and mount SDK paths
PROCESSED_ARGS=()
for arg in "${ARGS[@]}"; do
    # Check if argument looks like a path that exists on the host
    if [[ -d "$arg" ]]; then
        # Convert to absolute path
        ABS_PATH="$(cd "$arg" && pwd)"
        DOCKER_ARGS+=(-v "${ABS_PATH}:/sdk")
        PROCESSED_ARGS+=("/sdk")
    elif [[ -f "$arg" ]]; then
        # For files, mount the parent directory
        ABS_PATH="$(cd "$(dirname "$arg")" && pwd)"
        FILENAME="$(basename "$arg")"
        DOCKER_ARGS+=(-v "${ABS_PATH}:/sdk")
        PROCESSED_ARGS+=("/sdk/${FILENAME}")
    else
        PROCESSED_ARGS+=("$arg")
    fi
done

exec docker run "${DOCKER_ARGS[@]}" "${IMAGE}" "${PROCESSED_ARGS[@]}"
