#!/bin/bash
# Record demo GIF using Docker container
# Usage: OPENAI_API_KEY=your-key ./record-demo.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

# Check for OpenAI API key
if [ -z "$OPENAI_API_KEY" ]; then
    echo "Error: OPENAI_API_KEY environment variable is required"
    echo "Usage: OPENAI_API_KEY=your-key ./record-demo.sh"
    exit 1
fi

echo "Building demo recording container..."
docker build -f "$SCRIPT_DIR/Dockerfile.demo" \
    --build-arg CACHEBUST=$(date +%s) \
    -t sdk-chat-demo "$REPO_ROOT"

echo "Recording demo..."
docker run --rm \
    -v "$SCRIPT_DIR:/out" \
    -e OPENAI_API_KEY="$OPENAI_API_KEY" \
    -e SDK_CLI_USE_OPENAI=true \
    sdk-chat-demo

echo "Done! Demo saved to $SCRIPT_DIR/demo.gif"
ls -lh "$SCRIPT_DIR/demo.gif"
