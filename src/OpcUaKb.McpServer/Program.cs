using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

// ═══════════════════════════════════════════════════════════════════════
// OPC UA Knowledge Base — Custom MCP Server
// Exposes structured search tools over the Azure AI Search index.
//
// Supports two transport modes:
//   HTTP/SSE (default): Run as a web server for hosted deployment
//   stdio:              Pass --stdio for local Copilot CLI usage
//
// Required env vars: SEARCH_ENDPOINT, SEARCH_API_KEY
// Optional: SEARCH_INDEX_NAME (default: opcua-content-index)
// ═══════════════════════════════════════════════════════════════════════

var useStdio = args.Contains("--stdio");

if (useStdio)
{
    // stdio transport for local CLI usage
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton<SearchService>();
    builder.Services
        .AddMcpServer(o => o.ServerInfo = new() { Name = "opcua-kb", Version = "1.0.0" })
        .WithStdioServerTransport()
        .WithToolsFromAssembly();
    await builder.Build().RunAsync();
}
else
{
    // HTTP/SSE transport for hosted deployment
    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddSingleton<SearchService>();
    builder.Services
        .AddMcpServer(o => o.ServerInfo = new() { Name = "opcua-kb", Version = "1.0.0" })
        .WithHttpTransport(o => o.Stateless = true)
        .WithToolsFromAssembly();

    var app = builder.Build();
    app.MapMcp();
    app.Run();
}
