#!/bin/bash
# Docker entrypoint for demo recording
set -e

export VHS_NO_SANDBOX=true

# Write environment variables to bashrc so VHS subshells can access them
echo "export GH_TOKEN=\"$GH_TOKEN\"" >> ~/.bashrc
echo "export OPENAI_API_KEY=\"$OPENAI_API_KEY\"" >> ~/.bashrc

# Check if sdk-chat:latest exists (should be pre-built)
if ! docker image inspect sdk-chat:latest &>/dev/null; then
    echo "ERROR: sdk-chat:latest image not found."
    echo "Please build it first with: docker build -f Dockerfile.release -t sdk-chat:latest ."
    exit 1
fi

# Get the host workspace path (for Docker-in-Docker mounts)
HOST_WORKSPACE="${HOST_WORKSPACE_PATH:-}"
if [[ -z "$HOST_WORKSPACE" ]]; then
    echo "ERROR: HOST_WORKSPACE_PATH not set."
    echo "Set it to the host path that maps to /workspace in this container."
    exit 1
fi

# Get the host home path (for Copilot credentials)
HOST_HOME="${HOST_HOME_PATH:-}"
if [[ -z "$HOST_HOME" ]]; then
    echo "WARNING: HOST_HOME_PATH not set. Copilot credentials won't be mounted."
fi

# Set up scripts directory in home
mkdir -p ~/scripts

# Create a wrapper script that works in the demo environment
cat > ~/scripts/sdk-chat.sh << EOF
#!/bin/bash
# Simplified wrapper for demo
IMAGE="sdk-chat:latest"
HOST_WORKSPACE="$HOST_WORKSPACE"
HOST_HOME="$HOST_HOME"

DOCKER_ARGS=(--rm)

# Pass through environment variables
[[ -n "\${GH_TOKEN:-}" ]] && DOCKER_ARGS+=(-e "GH_TOKEN=\$GH_TOKEN")
[[ -n "\${OPENAI_API_KEY:-}" ]] && DOCKER_ARGS+=(-e "OPENAI_API_KEY=\$OPENAI_API_KEY")

# Mount Copilot credentials if available on host
if [[ -n "\$HOST_HOME" ]]; then
    DOCKER_ARGS+=(-v "\$HOST_HOME/.copilot:/root/.copilot:ro")
fi

# Check if --load-dotenv is in args, mount .env file if so
if [[ " \$* " == *" --load-dotenv "* ]]; then
    DOCKER_ARGS+=(-v "\$HOST_WORKSPACE/.env:/app/.env:ro")
fi

# Check for path arguments and mount them
FINAL_ARGS=()
for arg in "\$@"; do
    if [[ -d "\$arg" ]]; then
        # Map container path to host path for the openai-dotnet SDK
        if [[ "\$arg" == ~/openai-dotnet ]] || [[ "\$arg" == /root/openai-dotnet ]]; then
            # Use the temp/openai-dotnet from the workspace on the host
            DOCKER_ARGS+=(-v "\$HOST_WORKSPACE/temp/openai-dotnet:/sdk")
        else
            DOCKER_ARGS+=(-v "\$arg:/sdk")
        fi
        FINAL_ARGS+=("/sdk")
    else
        FINAL_ARGS+=("\$arg")
    fi
done

exec docker run "\${DOCKER_ARGS[@]}" "\$IMAGE" "\${FINAL_ARGS[@]}"
EOF
chmod +x ~/scripts/sdk-chat.sh

# Add scripts to PATH
export PATH="$HOME/scripts:$PATH"
echo 'export PATH="$HOME/scripts:$PATH"' >> ~/.bashrc

# Record the demo from home directory
cd ~
vhs /workspace/demo/demo.tape

# Copy output back to workspace
cp ~/demo.gif /workspace/demo/demo.gif 2>/dev/null || true

echo "Demo recorded to demo/demo.gif"
