#!/bin/bash
# Build the release artifact: the mod DLL + client helpers + README, zipped.
# Usage: ./package.sh [version]   (default 1.0.0)
set -e

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VERSION="${1:-1.0.0}"
DOTNET="/mnt/c/Program Files/dotnet/dotnet.exe"
OUT="$DIR/dist"
STAGE="$OUT/UnityExplorerTLD.MCP"

echo "Building release DLL..."
"$DOTNET" build "$DIR/UnityExplorerTLD.MCP.csproj" -c Release >/dev/null

echo "Staging files..."
rm -rf "$STAGE"
mkdir -p "$STAGE/client"
cp "$DIR/bin/Release/UnityExplorerTLD.MCP.dll" "$STAGE/"
cp "$DIR/client/proxy.js" "$DIR/client/setup.sh" "$DIR/client/setup.ps1" "$STAGE/client/"
cp "$DIR/README.md" "$STAGE/"

echo "Zipping..."
ZIPBASE="UnityExplorerTLD.MCP_${VERSION}"
python3 -c "import shutil; shutil.make_archive('$OUT/$ZIPBASE','zip','$OUT','UnityExplorerTLD.MCP')"

echo "Packaged: $OUT/$ZIPBASE.zip"
python3 -c "import zipfile;[print('  '+n) for n in zipfile.ZipFile('$OUT/$ZIPBASE.zip').namelist()]"
