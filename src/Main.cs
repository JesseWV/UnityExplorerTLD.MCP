using System;
using MelonLoader;

[assembly: MelonInfo(typeof(UnityExplorerTLD.MCP.Main), "UnityExplorerTLD.MCP", "1.0.0", "Lycanthor")]
[assembly: MelonGame("Hinterland", "TheLongDark")]
// Load after UnityExplorerTLD when it is present (we depend on its C# console).
[assembly: MelonOptionalDependencies("UnityExplorerTLD")]

namespace UnityExplorerTLD.MCP
{
    /// <summary>
    /// MelonLoader entry point for the UnityExplorerTLD MCP addon.
    /// This is a standalone mod that rides on top of the unmodified public
    /// UnityExplorerTLD build and exposes its C# console over an MCP server.
    /// </summary>
    public class Main : MelonMod
    {
        private static MelonPreferences_Category configCategory;
        private static MelonPreferences_Entry<bool> mcpEnabled;
        private static MelonPreferences_Entry<int> mcpPort;

        public override void OnInitializeMelon()
        {
            configCategory = MelonPreferences.CreateCategory("UnityExplorerMCP");
            mcpEnabled = configCategory.CreateEntry("Enabled", true, "Enable MCP Server");
            mcpPort = configCategory.CreateEntry("Port", McpServer.DEFAULT_PORT, "MCP Server Port");

            if (!mcpEnabled.Value)
            {
                LoggerInstance.Msg("MCP server disabled in config (UnityExplorerMCP.Enabled = false).");
                return;
            }

            try
            {
                McpServer.Start(mcpPort.Value, LoggerInstance);
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Failed to start MCP server: {ex}");
            }
        }

        // Code execution is marshalled onto the main thread here.
        public override void OnUpdate()
        {
            McpServer.Update();
        }

        public override void OnDeinitializeMelon()
        {
            McpServer.Stop();
        }
    }
}
