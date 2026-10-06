using iNKORE.UI.WPF.Modern.Mcp.Index;

namespace iNKORE.UI.WPF.Modern.Mcp;

/// <summary>Singleton shared by the tools: the index and the startup version check.</summary>
public sealed class ServerState(IndexStore store, VersionCheckResult startupCheck)
{
    public IndexStore Store { get; } = store;

    /// <summary>Version check of the folder the server was started in (the client's project).</summary>
    public VersionCheckResult StartupCheck { get; } = startupCheck;
}
