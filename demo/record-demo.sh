#!/bin/bash
# Record demo GIF using the project's dev container
# Usage: ./record-demo.sh
set -e
cd "$(dirname "${BASH_SOURCE[0]}")/.."

# Get GH_TOKEN from gh CLI
GH_TOKEN=$(grep oauth_token ~/.config/gh/hosts.yml 2>/dev/null | awk '{print $2}' | head -1)
[ -z "$GH_TOKEN" ] && { echo "Run 'gh auth login' first"; exit 1; }

docker build -t sdk-chat-dev .
docker run --rm -v "$(pwd):/workspace" -e GH_TOKEN="$GH_TOKEN" --entrypoint /workspace/demo/entrypoint.sh sdk-chat-dev

echo "Done!"
ls -lh demo/demo.gif
