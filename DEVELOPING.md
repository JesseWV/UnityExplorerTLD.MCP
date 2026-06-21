# Developing UnityExplorerTLD.MCP

Notes for building from source and working on the addon. **End users don't need any of this** — they
get the `.dll` from [Releases](../../releases).

## Building

```bash
./build_and_deploy.sh
```

Builds with the Windows .NET SDK (`dotnet.exe`) and copies the DLL into the game's `Mods/`. The game
must be **closed** to overwrite the loaded DLL. Override the install path if needed:

```bash
dotnet.exe build UnityExplorerTLD.MCP.csproj -c Release -p:TheLongDarkPath="D:\Games\TheLongDark"
```

Output: `bin/Release/UnityExplorerTLD.MCP.dll` (single file; dependencies are `Private=false`).

## Design: reflection-only bridge

The project has **no compile-time reference** to `UnityExplorerTLD.dll`. It binds to UnityExplorer's
C# console (`UnityExplorer.CSConsole.ConsoleController`) and log panel
(`UnityExplorer.UI.Panels.LogPanel`) entirely through runtime reflection (`src/McpServer.cs`).

Why: DZ's public **6.0.0 binary** (re-released 2026-01-03) diverged from the old source tag. In that
build `ConsoleController` is an **instance singleton** — static `_instance`, instance public
`Evaluate(string,bool)`, instance non-public `_evaluator` / `sreNotSupported` — not the static API the
source suggests. Reflection keeps the addon working across these changes; if a member is missing it
degrades to a clear "console not available" message instead of failing to load.

The console is force-initialized headlessly by touching `ConsoleController._panel` (and falling back to
`Init()`), so no in-game UI interaction is needed. `unity_execute_csharp` returns output synchronously
by snapshotting the `LogPanel.Logs` count, running the eval on the main thread, and returning the new
entries.

## Verifying the UnityExplorer API of a given build

Before changing the reflection bindings, confirm the real member shapes of the UE build you're
targeting with a metadata-only probe (no need to load IL2CPP dependencies):

```csharp
// dotnet console app referencing System.Reflection.MetadataLoadContext
using var mlc = new MetadataLoadContext(
    new PathAssemblyResolver(/* runtime dir + game MelonLoader/net6 + Il2CppAssemblies + Mods */),
    "System.Private.CoreLib");
var asm = mlc.LoadFromAssemblyPath(@"...\Mods\UnityExplorerTLD.dll");
var t = asm.GetType("UnityExplorer.CSConsole.ConsoleController");
// enumerate t.GetMethods/GetProperties/GetFields with BindingFlags.DeclaredOnly | NonPublic | Public
//           | Static | Instance, printing static/instance + signatures.
```

## Client helpers

`client/` is shipped to end users in the release zip (it's how they connect), but it's plain
scripts — no build step:

- `proxy.js` — Node stdio↔HTTP MCP proxy (WSL NAT). Built-ins only.
- `setup.sh` — WSL registrar; auto-detects environment.
- `setup.ps1` — Windows host setup (firewall + URL ACL), run elevated.
