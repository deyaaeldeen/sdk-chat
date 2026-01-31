#!/bin/bash
# Docker entrypoint for demo recording
set -e

export DOTNET_ROOT=/usr/share/dotnet
export PATH="$PATH:$HOME/.dotnet/tools:$HOME/.local/bin"
export VHS_NO_SANDBOX=true

# Write GH_TOKEN to bashrc so VHS subshells can access it
echo "export GH_TOKEN=\"$GH_TOKEN\"" >> ~/.bashrc

# Uninstall any existing tool
dotnet tool uninstall -g microsoft.sdkchat 2>/dev/null || true

# Pack and install from bind-mounted workspace
dotnet pack Microsoft.SdkChat -o ./nupkg -c Release
dotnet tool install -g --add-source ./nupkg Microsoft.SdkChat

# Copy .env to home for --load-dotenv to find it
cp /workspace/.env ~/.env 2>/dev/null || true

# Record the demo
vhs demo/demo.tape

echo "Demo recorded to demo/demo.gif"
