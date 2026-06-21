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
2. Drop `UnityExplorerTLD.MCP.dll` into the game's `Mods/` folder.
3. Launch the game.

The MCP server starts automatically and logs its reachable addresses to the MelonLoader console.

> **Windows URL ACL:** the server binds to `http://+:<port>/`. If `HttpListener` fails to start with
> an access error, reserve the URL once (admin PowerShell):
> `netsh http add urlacl url=http://+:3000/ user=Everyone`
> — or run the game elevated.

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

The server speaks MCP over HTTP. **Point your client at `http://localhost:3000/`** — use that
single stable URL in every setup below (never a baked-in IP, which breaks across reboots).

| Where your MCP client runs | What you need |
|---|---|
| **Windows** (Claude Desktop, Claude Code for Windows, …) | Nothing — `localhost:3000` already reaches the game. No firewall rule, no IP. |
| **WSL with mirrored networking** (Windows 11 22H2+) | One-time: add `[wsl2]`/`networkingMode=mirrored` to `%UserProfile%\.wslconfig`, then `wsl --shutdown`. `localhost:3000` then reaches the host directly. |
| **WSL with NAT networking** (default / Windows 10) | Run the bridge: `python3 tools/wsl-bridge.py` (keeps `localhost:3000` pointed at the current Windows-host gateway, which changes across reboots). Also add a one-time Windows inbound firewall rule for TCP 3000 (elevated PowerShell): `New-NetFirewallRule -DisplayName "TLD MCP" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 3000`. |

Register with Claude Code (any of the above):

```bash
claude mcp add --transport http unity-explorer http://localhost:3000/
```

### Autostarting the WSL bridge (NAT users)

The bridge only needs to run while you use the MCP client. To start it automatically, add to `~/.bashrc`:

```bash
pgrep -f wsl-bridge.py >/dev/null || (python3 /path/to/UnityExplorerTLD.MCP/tools/wsl-bridge.py &>/dev/null &)
```

It requires only `python3` and resolves the Windows-host gateway live per connection, so it
survives reboots and IP changes with no reconfiguration.

## Security

The server validates the `Origin` header (DNS-rebinding protection): browser requests from non-local
origins are rejected; non-browser clients (Claude Code, curl) are unaffected. It still binds to all
interfaces so a WSL client can reach it — only run it on trusted networks.

## Building

```bash
./build_and_deploy.sh
```

Builds with the Windows .NET SDK and copies the DLL into the game's `Mods/`. Override the game path
with `-p:TheLongDarkPath="..."` if your install differs. The project intentionally has **no compile-time
reference** to `UnityExplorerTLD.dll` — the bridge is reflection-only.
