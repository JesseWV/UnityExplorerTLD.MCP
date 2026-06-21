#!/bin/bash
# UnityExplorerTLD.MCP - register the in-game MCP server with Claude Code (WSL).
#
# Auto-detects the right transport for your environment and registers it:
#   - HTTP localhost  -> Windows-side client, or WSL mirrored networking
#   - stdio proxy     -> WSL NAT (default); also runs the one-time host setup
#
# Force a mode:  ./setup.sh http   |   ./setup.sh stdio
# For best auto-detection, have The Long Dark running with the mod first.
set -e

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROXY="$DIR/proxy.js"
PORT="${UE_MCP_PORT:-3000}"
MODE="$1"
NAME="unity-explorer"

probe() { # $1=host -> 0 if the MCP server answers
  node -e "const h=require('http');const r=h.request({host:'$1',port:$PORT,method:'POST',path:'/',timeout:2500,headers:{'Content-Type':'application/json'}},x=>process.exit(0));r.on('error',()=>process.exit(1));r.on('timeout',()=>process.exit(1));r.end('{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}');" 2>/dev/null
}

host_setup() { # firewall + URL ACL via one elevated PowerShell (UAC prompt)
  local winps; winps="$(wslpath -w "$DIR/setup.ps1")"
  echo "Requesting one-time Windows host setup (firewall + URL ACL) - accept the UAC prompt..."
  powershell.exe -NoProfile -Command \
    "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','$winps','-Port','$PORT'" \
    2>/dev/null || echo "  Could not auto-launch; run client/setup.ps1 as Administrator on Windows."
}

if [ -z "$MODE" ]; then
  echo "Detecting connection mode..."
  if probe 127.0.0.1; then
    MODE=http
  else
    GW="$(ip route show default 2>/dev/null | awk '{print $3}')"
    if [ -n "$GW" ] && probe "$GW"; then
      MODE=stdio          # reachable via gateway -> NAT, host already set up
    else
      MODE=stdio; DO_HOST_SETUP=1   # can't reach (game off, or no firewall) -> WSL default
    fi
  fi
fi

claude mcp remove "$NAME" -s local >/dev/null 2>&1 || true

if [ "$MODE" = "http" ]; then
  claude mcp add --transport http "$NAME" "http://localhost:$PORT/"
  echo "Registered '$NAME' over HTTP (localhost:$PORT) - Windows client or WSL mirrored."
else
  [ -n "$DO_HOST_SETUP" ] && host_setup
  claude mcp add "$NAME" -- node "$PROXY"
  echo "Registered '$NAME' over the stdio proxy - WSL NAT."
  echo "If it can't connect: ensure the firewall rule exists (run client/setup.ps1 as admin)."
fi
