using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
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
//           AOAI_ENDPOINT — enables search_docs_rag tool (KB retrieve + GPT-4o)
//           AOAI_API_KEY  — AOAI key auth (falls back to Managed Identity)
//           KB_NAME       — knowledge base name (default: opcua-kb)
//
// Rate limiting env vars:
//   MCP_API_KEY           — API key for authenticated access
//   MCP_REQUIRE_AUTH      — "true" to reject all unauthenticated requests
//   MCP_ANON_RATE_LIMIT   — Max requests/min for anonymous callers (default: 10)
//   MCP_AUTH_RATE_LIMIT   — Max requests/min for authenticated callers (default: 0 = unlimited)
// ═══════════════════════════════════════════════════════════════════════

var useStdio = args.Contains("--stdio");

if (useStdio)
{
    // stdio transport for local CLI usage — no auth or rate limiting needed
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton<SearchService>();
    builder.Services.AddSingleton<KbService>();
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
    builder.Services.AddSingleton<KbService>();
    builder.Services
        .AddMcpServer(o => o.ServerInfo = new() { Name = "opcua-kb", Version = "1.0.0" })
        .WithHttpTransport(o => o.Stateless = true)
        .WithToolsFromAssembly();

    // Configuration
    var apiKey = Environment.GetEnvironmentVariable("MCP_API_KEY")
        ?? Environment.GetEnvironmentVariable("SEARCH_API_KEY");
    var requireAuth = string.Equals(
        Environment.GetEnvironmentVariable("MCP_REQUIRE_AUTH"), "true", StringComparison.OrdinalIgnoreCase);
    var anonRateLimit = int.TryParse(Environment.GetEnvironmentVariable("MCP_ANON_RATE_LIMIT"), out var arl) ? arl : 10;
    var authRateLimit = int.TryParse(Environment.GetEnvironmentVariable("MCP_AUTH_RATE_LIMIT"), out var atrl) ? atrl : 0;

    // Rate limiting — partitioned by authenticated vs anonymous (per-IP)
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = 429;
        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.ContentType = "application/json";
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
            await context.HttpContext.Response.WriteAsync(
                """{"jsonrpc":"2.0","error":{"code":-32000,"message":"Rate limit exceeded. Provide an api-key header for higher limits."},"id":""}""", ct);
        };

        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var hasValidKey = !string.IsNullOrEmpty(apiKey)
                && context.Request.Headers.TryGetValue("api-key", out var key)
                && key == apiKey;

            if (hasValidKey)
            {
                // Authenticated tier — unlimited or configurable
                return authRateLimit > 0
                    ? RateLimitPartition.GetFixedWindowLimiter("authenticated", _ =>
                        new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = authRateLimit,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        })
                    : RateLimitPartition.GetNoLimiter("authenticated");
            }

            // Anonymous tier — rate limited per IP
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter($"anon:{ip}", _ =>
                new FixedWindowRateLimiterOptions
                {
                    PermitLimit = anonRateLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
        });
    });

    var app = builder.Build();

    // Middleware order: rate limiting → OAuth endpoint handling → auth → MCP
    app.UseRateLimiter();

    // Handle OAuth discovery and fallback endpoints explicitly.
    // MCP clients MUST check /.well-known/oauth-authorization-server per RFC 8414.
    // Returning 404 tells clients there is no authorization server — auth is not required.
    // The fallback /authorize, /token, /register paths return 400 with a clear message
    // instead of falling through to the MCP handler (which would return confusing errors).
    app.Map("/authorize", () => Results.Json(
        new { error = "unsupported_grant_type", error_description = "This server uses api-key header authentication. OAuth is not supported." },
        statusCode: 400));
    app.Map("/token", () => Results.Json(
        new { error = "unsupported_grant_type", error_description = "This server uses api-key header authentication. OAuth is not supported." },
        statusCode: 400));
    app.Map("/register", () => Results.Json(
        new { error = "invalid_client", error_description = "This server uses api-key header authentication. OAuth is not supported." },
        statusCode: 400));

    // Auth middleware — block or allow anonymous based on config
    if (!string.IsNullOrEmpty(apiKey))
    {
        app.Use(async (context, next) =>
        {
            var hasValidKey = context.Request.Headers.TryGetValue("api-key", out var providedKey)
                && providedKey == apiKey;

            if (!hasValidKey && requireAuth)
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"jsonrpc":"2.0","error":{"code":-32000,"message":"Unauthorized: provide a valid api-key header"},"id":""}""");
                return;
            }

            await next();
        });
    }

    app.MapMcp();
    app.Run();
}
