using iNKORE.UI.WPF.Modern.Mcp;
using iNKORE.UI.WPF.Modern.Mcp.Index;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

var store = IndexStore.LoadEmbedded();

if (args is ["--version"])
{
    Console.WriteLine($"inkore-wpf-mcp {store.Metadata.LibraryVersion} (docs {store.Metadata.DocsCommit?[..7] ?? "none"})");
    return;
}

// Clients start stdio servers in the user's project folder; check which library version it uses.
var versionCheck = ProjectVersionCheck.Check(Directory.GetCurrentDirectory(), store.Metadata.LibraryVersion);

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol, so all logging goes to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new ServerState(store, versionCheck));
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "inkore-wpf-modern", Version = store.Metadata.LibraryVersion };
        options.ServerInstructions = ServerInstructions.Build(store.Metadata, versionCheck);
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
