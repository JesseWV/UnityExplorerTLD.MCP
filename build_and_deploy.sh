#!/bin/bash
# Build the UnityExplorerTLD.MCP addon and deploy it to The Long Dark's Mods folder.
set -e

DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
GAME="/mnt/c/Program Files (x86)/Steam/steamapps/common/TheLongDark"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "Building UnityExplorerTLD.MCP..."
"$DOTNET" build "$SCRIPT_DIR/UnityExplorerTLD.MCP.csproj" -c Release

echo "Deploying to Mods folder..."
cp "$SCRIPT_DIR/bin/Release/UnityExplorerTLD.MCP.dll" "$GAME/Mods/UnityExplorerTLD.MCP.dll"

echo "Done."
ls -lh "$GAME/Mods/UnityExplorerTLD.MCP.dll"
