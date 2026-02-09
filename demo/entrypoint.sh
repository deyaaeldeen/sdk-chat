#!/bin/bash
# Docker entrypoint for demo recording
set -e

export VHS_NO_SANDBOX=true

# Write environment variables to bashrc so VHS subshells can access them
echo "export GH_TOKEN=\"$GH_TOKEN\"" >> ~/.bashrc
echo "export OPENAI_API_KEY=\"$OPENAI_API_KEY\"" >> ~/.bashrc

# Record the demo from workspace directory
cd /workspace
vhs /workspace/demo/demo.tape

# Copy output back to workspace
cp demo.gif /workspace/demo/demo.gif 2>/dev/null || true

echo "Demo recorded to demo/demo.gif"
