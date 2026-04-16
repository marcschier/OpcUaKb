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

    // API key authentication middleware
    var apiKey = Environment.GetEnvironmentVariable("MCP_API_KEY")
        ?? Environment.GetEnvironmentVariable("SEARCH_API_KEY");
    if (!string.IsNullOrEmpty(apiKey))
    {
        app.Use(async (context, next) =>
        {
            if (!context.Request.Headers.TryGetValue("api-key", out var providedKey)
                || providedKey != apiKey)
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"jsonrpc":"2.0","error":{"code":-32000,"message":"Unauthorized: provide a valid api-key header"},"id":""}""");
                return;
            }
            await next();
        });
    }

    app.MapMcp();
    app.Run();
}
