#!/bin/bash
# Record demo GIF using Docker container with local repo bind-mounted
# Usage: ./record-demo.sh
# Requires: GH_TOKEN in environment or gh CLI authenticated

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

# Extract GitHub token from gh CLI config for Copilot mode
GH_TOKEN=""
if [ -f "$HOME/.config/gh/hosts.yml" ]; then
    GH_TOKEN=$(grep oauth_token "$HOME/.config/gh/hosts.yml" 2>/dev/null | awk '{print $2}' | head -1)
fi

if [ -z "$GH_TOKEN" ]; then
    echo "Error: GH_TOKEN not found. Run 'gh auth login' first."
    exit 1
fi

echo "Building demo recording container..."
docker build -f "$SCRIPT_DIR/Dockerfile.demo" -t sdk-chat-demo "$REPO_ROOT"

echo "Recording demo (using local repo)..."
docker run --rm \
    -v "$REPO_ROOT:/workspace" \
    -e GH_TOKEN="$GH_TOKEN" \
    sdk-chat-demo

echo "Done! Demo saved to $SCRIPT_DIR/demo.gif"
ls -lh "$SCRIPT_DIR/demo.gif"
