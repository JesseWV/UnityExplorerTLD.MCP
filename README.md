# UnityExplorerTLD.MCP

An **addon** for [UnityExplorerTLD](https://github.com/DigitalzombieTLD/UnityExplorerTLD) that exposes
its C# console to AI agents (Claude Code, etc.) over a small **MCP (Model Context Protocol)** server.

It is a standalone MelonLoader mod that rides on top of the **unmodified** public UnityExplorerTLD
build — it does not fork or repackage UnityExplorer. It binds to UnityExplorer entirely through
runtime reflection, so it keeps working across UnityExplorer updates.

## Requirements

- The Long Dark with **MelonLoader** (net6 / IL2CPP)
- **UnityExplorerTLD** installed (any recent build; the public 6.0.0 release works)

## Installation

1. Install UnityExplorerTLD as usual (`Mods/UnityExplorerTLD.dll`, plus its `UserLibs`).
2. Download the latest [release](../../releases) and drop `UnityExplorerTLD.MCP.dll` into the game's
   `Mods/` folder. (The release zip also contains the `client/` helpers used to connect — see below.)
3. Launch the game.

The MCP server starts automatically and logs its reachable addresses to the MelonLoader console.

> If the server fails to start with a URL-access error, or a non-Windows client can't reach it, run the
> one-time host setup as Administrator (`client/setup.ps1`) — it adds the `http://+:3000/` URL ACL and a
> firewall rule. WSL users can let `client/setup.sh` handle this automatically.

## Configuration

`MelonLoader/UserData/MelonPreferences.cfg`:

```ini
[UnityExplorerMCP]
Enabled = true
Port = 3000
```

## What it exposes

A spec-aligned MCP server (JSON-RPC 2.0 over HTTP POST; protocol revision `2025-06-18`, with
`2025-03-26` / `2024-11-05` accepted for compatibility) with two tools:

- **`unity_execute_csharp`** — runs C# in the live game via UnityExplorer's console and returns
  **that command's output directly in the response** (REPL return value, anything the code logged,
  and any compile/runtime errors). No separate read step, no polling. Output produced later by
  coroutines/async still goes to the log — use `unity_read_log` for that.
- **`unity_read_log`** — reads recent entries from UnityExplorer's log panel.

## Connecting your MCP client

The MCP server runs inside the game on Windows. How a client reaches it depends only on where the
**client** runs. All three supported environments are covered — the address never needs an IP.

| # | MCP client environment | Transport | Setup |
|---|---|---|---|
| 1 | **Windows** (Claude Desktop, etc.) | HTTP → `http://localhost:3000/` | none — localhost reaches the game directly |
| 2 | **WSL, mirrored networking** (Win11 22H2+) | HTTP → `http://localhost:3000/` | one-time `.wslconfig`: `[wsl2]` / `networkingMode=mirrored`, then `wsl --shutdown` |
| 3 | **WSL, NAT networking** (default / Win10) | **stdio proxy** (`client/proxy.js`) | run host setup once (firewall + URL ACL); the proxy resolves the host gateway live, so reboots don't break it |

### Easiest: let the setup script pick (WSL)

```bash
client/setup.sh            # auto-detects env 1/2/3 and registers 'unity-explorer'
# client/setup.sh http     # force HTTP localhost (env 1/2)
# client/setup.sh stdio    # force the stdio proxy (env 3)
```

For env 3 it will trigger the one-time Windows host setup (`client/setup.ps1`, run elevated via a UAC
prompt) which adds the firewall rule and the `http://+:3000/` URL ACL.

### Manual registration

```bash
# env 1 (Windows client) or env 2 (WSL mirrored):
claude mcp add --transport http unity-explorer http://localhost:3000/

# env 3 (WSL NAT) — Claude Code launches the proxy itself; no IP, no background process:
claude mcp add unity-explorer -- node /path/to/UnityExplorerTLD.MCP/client/proxy.js
```

The stdio proxy (`client/proxy.js`) needs only Node (already present with Claude Code) — no extra
dependency, and Claude Code starts/stops it with the session.

## Security

The server validates the `Origin` header (DNS-rebinding protection): browser requests from non-local
origins are rejected; non-browser clients (Claude Code, curl) are unaffected. It still binds to all
interfaces so a WSL client can reach it — only run it on trusted networks.
