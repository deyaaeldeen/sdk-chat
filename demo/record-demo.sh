#!/bin/bash
# Record demo GIF using Docker container
# Usage: ./record-demo.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

echo "Building demo recording container..."
docker build -f "$SCRIPT_DIR/Dockerfile.demo" -t sdk-chat-demo "$REPO_ROOT"

echo "Recording demo..."
docker run --rm -v "$SCRIPT_DIR:/out" sdk-chat-demo

echo "Done! Demo saved to $SCRIPT_DIR/demo.gif"
ls -lh "$SCRIPT_DIR/demo.gif"
