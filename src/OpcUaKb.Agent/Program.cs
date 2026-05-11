using Microsoft.Agents.Authentication.Msal;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using OpcUaKb.Agent;

// ═══════════════════════════════════════════════════════════════════════
// OPC UA Expert Agent — ASP.NET Core hosting for a Microsoft 365 Agents
// SDK custom engine agent. Replaces the OpcUaKb.Chat console chatbot.
// Hosted on Azure Container Apps; reuses KbService from OpcUaKb.Core.
// ═══════════════════════════════════════════════════════════════════════

var builder = WebApplication.CreateBuilder(args);

// ───────────────────────────────────────────────────────────────────────
// Wire env-var bot credentials into IConfiguration so MSAL + token
// validation can resolve them.
//
// Three modes are supported:
//
//   1. UserManagedIdentity (default in Azure): BOT_AUTH_TYPE=UserManagedIdentity,
//      BOT_ID=<UAMI clientId>. No secret needed; MSAL uses the container's
//      assigned user-managed identity to acquire tokens.
//
//   2. ClientSecret (legacy / non-Microsoft tenants): BOT_AUTH_TYPE=ClientSecret
//      (or unset), BOT_ID=<Entra appId>, BOT_PASSWORD=<client secret>.
//
//   3. Anonymous (local dev / Teams App Test Tool): BOT_ID empty.
// ───────────────────────────────────────────────────────────────────────
var botId = Environment.GetEnvironmentVariable("BOT_ID")
         ?? Environment.GetEnvironmentVariable("MicrosoftAppId")
         ?? "";
var botPassword = Environment.GetEnvironmentVariable("BOT_PASSWORD")
               ?? Environment.GetEnvironmentVariable("MicrosoftAppPassword")
               ?? "";
var botTenantId = Environment.GetEnvironmentVariable("BOT_TENANT_ID") ?? "";
var botAuthType = Environment.GetEnvironmentVariable("BOT_AUTH_TYPE");
if (string.IsNullOrWhiteSpace(botAuthType))
{
    botAuthType = string.IsNullOrWhiteSpace(botPassword) ? "ClientSecret" : "ClientSecret";
}

builder.Configuration["TokenValidation:Audiences:0"] = botId;
builder.Configuration["Connections:ServiceConnection:Settings:AuthType"] = botAuthType;
builder.Configuration["Connections:ServiceConnection:Settings:ClientId"] = botId;
builder.Configuration["Connections:ServiceConnection:Settings:ClientSecret"] = botPassword;
if (!string.IsNullOrWhiteSpace(botTenantId))
{
    builder.Configuration["Connections:ServiceConnection:Settings:TenantId"] = botTenantId;
    builder.Configuration["Connections:ServiceConnection:Settings:AuthorityEndpoint"] =
        $"https://login.microsoftonline.com/{botTenantId}";
}

// ───────────────────────────────────────────────────────────────────────
// Core ASP.NET Core services
// ───────────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient();
builder.Services.AddControllers();
builder.Services.AddRouting();

// ───────────────────────────────────────────────────────────────────────
// Microsoft 365 Agents SDK wiring
// ───────────────────────────────────────────────────────────────────────
// MSAL authentication for outbound channel-service calls (uses the
// Connections:ServiceConnection section in appsettings.json).
builder.Services.AddDefaultMsalAuth(builder.Configuration);

// AgentApplicationOptions, CloudAdapter, channel services, etc.
builder.AddAgentApplicationOptions();
builder.AddAgent<OpcUaAgent>();

// In-memory turn state store. Production agents that need to survive
// restarts should switch to a persisted IStorage implementation.
builder.Services.AddSingleton<IStorage, MemoryStorage>();

// ───────────────────────────────────────────────────────────────────────
// OPC UA Knowledge Base services (read SEARCH_*, AOAI_*, KB_NAME,
// GPT_DEPLOYMENT env vars internally).
//
//   SearchService        — shared Azure AI Search client used by every
//                          structured tool (search_nodes, count_nodes, …).
//   KbService            — KB retrieve + GPT-4o synthesis used by the
//                          search_docs_rag fallback tool.
//   AoaiChatClient       — raw chat-completions HTTP client used by the
//                          tool-using agent loop in OpcUaAgent.
//   AgentToolDispatcher  — reflects [McpServerTool] methods in OpcUaKb.Core
//                          and bridges them into OpenAI function-calling.
// ───────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<SearchService>();
builder.Services.AddSingleton<KbService>();
builder.Services.AddSingleton<AoaiChatClient>();
builder.Services.AddSingleton<AgentToolDispatcher>();

var app = builder.Build();

app.UseHeaderPropagation();
app.UseRouting();

// Health endpoint — Azure Container Apps + load balancer probe target.
app.MapGet("/", () => Results.Ok("OPC UA Expert Agent is running"));

// Bot Framework messaging endpoint — entry point for all Bot Framework
// activities (Teams, Web Chat, Direct Line, Test Tool).
app.MapPost("/api/messages", async (
    HttpRequest request,
    HttpResponse response,
    IAgentHttpAdapter adapter,
    IAgent agent,
    CancellationToken cancellationToken) =>
{
    await adapter.ProcessAsync(request, response, agent, cancellationToken);
});

// ───────────────────────────────────────────────────────────────────────
// Bind port: PORT env var (Container Apps / Cloud Run convention) or
// 3978 default (Bot Framework convention).
// ───────────────────────────────────────────────────────────────────────
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    app.Urls.Clear();
    app.Urls.Add($"http://0.0.0.0:{port}");
}

app.Run();
