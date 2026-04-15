using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// ═══════════════════════════════════════════════════════════════════════
// OPC UA Knowledge Base — Custom MCP Server
// Exposes structured search tools over the Azure AI Search index.
// Run via stdio transport (invoked by Copilot CLI / MCP clients).
//
// Required env vars: SEARCH_ENDPOINT, SEARCH_API_KEY
// Optional: SEARCH_INDEX_NAME (default: opcua-content-index)
// ═══════════════════════════════════════════════════════════════════════

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<SearchService>();

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new()
        {
            Name = "opcua-kb",
            Version = "1.0.0",
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
