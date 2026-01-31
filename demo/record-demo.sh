#!/bin/bash
# Record demo GIF using Docker container with local repo bind-mounted
# Usage: ./record-demo.sh
# The script will read OPENAI_API_KEY from ../.env if not set

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

# Try to load from .env if OPENAI_API_KEY not set
if [ -z "$OPENAI_API_KEY" ] && [ -f "$REPO_ROOT/.env" ]; then
    echo "Loading API keys from .env..."
    export $(grep -E '^(OPENAI_API_KEY|GH_TOKEN)=' "$REPO_ROOT/.env" | xargs)
fi

# Check for OpenAI API key
if [ -z "$OPENAI_API_KEY" ]; then
    echo "Error: OPENAI_API_KEY not found"
    echo "Set it in the environment or in $REPO_ROOT/.env"
    exit 1
fi

# Extract GitHub token from gh CLI config for Copilot SDK auth
GH_TOKEN=""
if [ -f "$HOME/.config/gh/hosts.yml" ]; then
    GH_TOKEN=$(grep oauth_token "$HOME/.config/gh/hosts.yml" 2>/dev/null | awk '{print $2}' | head -1)
fi

echo "Building demo recording container..."
docker build -f "$SCRIPT_DIR/Dockerfile.demo" -t sdk-chat-demo "$REPO_ROOT"

echo "Recording demo (using local repo)..."
docker run --rm \
    -v "$REPO_ROOT:/workspace" \
    -e OPENAI_API_KEY="$OPENAI_API_KEY" \
    -e GH_TOKEN="$GH_TOKEN" \
    -e SDK_CLI_USE_OPENAI=true \
    sdk-chat-demo

echo "Done! Demo saved to $SCRIPT_DIR/demo.gif"
ls -lh "$SCRIPT_DIR/demo.gif"
