#!/bin/bash
# Register UnityExplorerTLD.MCP with Claude Code. Run from WSL:
#
#   bash setup.sh          # default: the stdio proxy. Works for WSL NAT *and*
#                          # mirrored networking (the proxy finds the host at
#                          # runtime). The game does NOT need to be running.
#   bash setup.sh http     # register HTTP http://localhost:3000/ instead
#                          # (Windows-side client, or WSL mirrored networking)
#
# The default also runs a one-time Windows host setup (firewall rule + URL ACL)
# via an elevated PowerShell - you'll get a single UAC prompt.
set -e

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROXY="$DIR/proxy.js"
PORT="${UE_MCP_PORT:-3000}"
MODE="${1:-stdio}"
NAME="unity-explorer"

host_setup() {
  local winps; winps="$(wslpath -w "$DIR/setup.ps1")"
  echo "Running one-time Windows host setup (firewall + URL ACL) - accept the UAC prompt..."
  powershell.exe -NoProfile -Command \
    "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','$winps','-Port','$PORT'" \
    2>/dev/null || echo "  Could not auto-launch; run client/setup.ps1 as Administrator on Windows."
}

command -v claude >/dev/null || { echo "Claude Code CLI ('claude') not found on PATH."; exit 1; }
claude mcp remove "$NAME" -s local >/dev/null 2>&1 || true

if [ "$MODE" = "http" ]; then
  claude mcp add --transport http "$NAME" "http://localhost:$PORT/"
  echo "Registered '$NAME' over HTTP localhost:$PORT (Windows client / WSL mirrored)."
else
  host_setup
  claude mcp add "$NAME" -- node "$PROXY"
  echo "Registered '$NAME' (stdio proxy)."
fi

echo "Done. Launch The Long Dark with the mod, then use 'unity-explorer' from Claude Code."
