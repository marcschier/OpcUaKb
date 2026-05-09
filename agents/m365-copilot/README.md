# OPC UA Expert — Microsoft 365 Copilot Agent

A declarative agent for Microsoft 365 Copilot that exposes the OPC UA Knowledge Base via the deployed [MCP server](../../src/OpcUaKb.McpServer/). Once installed, users can chat with **OPC UA Expert** directly inside Microsoft 365 Copilot Chat, Teams, Word, PowerPoint, or Outlook to search specifications, validate NodeSets, compare versions, and get modelling guidance — all grounded in 180,000+ indexed OPC UA documents.

## How It Works

```
User in Teams/Word/Copilot
        ↓
  Microsoft 365 Copilot orchestrator (GPT-4o)
        ↓ (calls MCP tools)
  opcua-kb-mcp-server (Azure Container Apps)
        ↓
  Azure AI Search index + Azure AI Foundry GPT-4o (RAG)
```

The declarative agent is just a JSON manifest package — no code or hosting. Microsoft 365 Copilot uses our existing MCP server (already deployed) as its backend. When a user prompts the agent, Copilot:

1. Maps the prompt to one or more of the 11 MCP tools (semantic matching when >5 plugins)
2. Calls the tool via standard MCP `tools/call` HTTP POST to our server
3. Receives the result, synthesizes a response using GPT-4o, and renders it in the Copilot UI

## Prerequisites

- A Microsoft 365 tenant with Microsoft 365 Copilot licensed users (or [Microsoft 365 Copilot developer license](https://learn.microsoft.com/microsoft-365-copilot/extensibility/prerequisites))
- Tenant admin must enable [Custom App Upload](https://learn.microsoft.com/microsoft-365-copilot/extensibility/prerequisites#requirements-for-copilot-extensibility-options) for sideloading
- [Visual Studio Code](https://code.visualstudio.com/)
- [Microsoft 365 Agents Toolkit](https://aka.ms/M365AgentsToolkit) extension for VS Code (version 6.3 or later)

## Folder Structure

```
agents/m365-copilot/
├── README.md                        # This file
├── m365agents.yml                   # Provisioning lifecycle for dev/prod
├── m365agents.local.yml             # Provisioning for local debug
├── env/
│   ├── .env.dev                     # Dev environment config
│   └── .env.local                   # Local environment config
├── instructions.md                  # Agent system prompt (referenced from declarativeAgent.json)
├── generate-icons.py                # Helper script to (re)generate icons
└── appPackage/
    ├── manifest.json                # Microsoft 365 app manifest (Teams app)
    ├── declarativeAgent.json        # Declarative agent definition
    ├── aiPlugin.json                # MCP plugin manifest (points to MCP server)
    ├── mcp-tools.json               # MCP tool definitions (11 tools)
    ├── color.png                    # 192×192 color icon
    └── outline.png                  # 32×32 outline icon
```

## Quick Start: Sideload to a Dev Tenant

1. **Open the folder in VS Code**:
   ```bash
   cd agents/m365-copilot
   code .
   ```

2. **Install the Microsoft 365 Agents Toolkit extension** (if not already installed). It's available in the VS Code Marketplace.

3. **Sign in** in the Agents Toolkit sidebar:
   - Click the Agents Toolkit icon in the Activity Bar
   - In the **Accounts** pane, select **Sign in to Microsoft 365**
   - Confirm both **Custom App Upload Enabled** and **Copilot Access Enabled** appear under your account

4. **Provision** in the **Lifecycle** pane:
   - Click **Provision** — this validates the manifest, packages it as a zip, and uploads to your tenant
   - When prompted for environment, select **dev**

5. **Test the agent**:
   - Go to https://m365.cloud.microsoft/chat
   - In the **Agents** sidebar, find **OPC UA Expert** and select it
   - Try a conversation starter, e.g. "What ObjectTypes does the Pumps companion specification define?"

## Publishing to the Tenant App Catalog

After validating in your dev tenant, you can publish to your organization's App Catalog so all licensed users can find it:

1. In the Agents Toolkit Lifecycle pane, click **Publish**
2. The package will be submitted to the tenant App Catalog for admin approval
3. A tenant administrator must approve via the [Microsoft 365 Admin Center](https://admin.microsoft.com/) → **Settings** → **Integrated apps**

## Publishing to AppSource (Public Distribution)

For public distribution to other organizations, submit through [Partner Center](https://partner.microsoft.com/dashboard/marketplaceoffers) following the [Teams Store validation guidelines](https://learn.microsoft.com/microsoftteams/platform/concepts/deploy-and-publish/appsource/prepare/teams-store-validation-guidelines). The agent must pass Microsoft's Responsible AI validation and security review.

## Configuration

### MCP Server URL

The plugin manifest (`appPackage/aiPlugin.json`) currently points to:
```
https://opcua-kb-mcp-server.salmonfield-436bb4c2.eastus.azurecontainerapps.io/
```

If you've deployed the MCP server to a different domain, update this URL in `aiPlugin.json` and the `validDomains` array in `manifest.json`.

### Authentication

The plugin currently uses **anonymous** access (`"auth": { "type": "None" }`). Our MCP server returns 200 for unauthenticated requests with rate limiting (100 requests/min per IP, configurable via `MCP_ANON_RATE_LIMIT`).

For production hardening, switch to **Microsoft Entra ID SSO** authentication:
1. Register an Entra app for the MCP server with App ID URI matching your server URL
2. Add token validation middleware to `src/OpcUaKb.McpServer/Program.cs`
3. Register an SSO client in [Teams Developer Portal](https://dev.teams.microsoft.com/tools)
4. Update `aiPlugin.json` to use:
   ```json
   "auth": {
     "type": "OAuthPluginVault",
     "reference_id": "<sso-registration-id>"
   }
   ```

See [Authentication for plugins](https://learn.microsoft.com/microsoft-365-copilot/extensibility/api-plugin-authentication) for details.

### Updating Tool Definitions

When tools are added, removed, or have their signatures changed in `OpcUaKb.McpServer`, regenerate `mcp-tools.json` from the live server:

```powershell
$key = az search admin-key show --service-name opcua-kb-search -g rg-opcua-kb --query primaryKey -o tsv
$url = "https://opcua-kb-mcp-server.salmonfield-436bb4c2.eastus.azurecontainerapps.io/"
$headers = @{"api-key"=$key; "Content-Type"="application/json"; "Accept"="application/json, text/event-stream"}
$body = '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
$r = Invoke-WebRequest -Method Post -Uri $url -Headers $headers -Body $body
$lines = $r.Content -split "`n" | Where-Object { $_ -match '^data: ' } | ForEach-Object { $_ -replace '^data: ','' }
foreach ($l in $lines) {
    $j = $l | ConvertFrom-Json
    if ($j.result.tools) { $j.result | ConvertTo-Json -Depth 10 | Out-File appPackage/mcp-tools.json -Encoding utf8 }
}
```

### Regenerating Icons

If you need to regenerate icons (e.g., size or design changes):

```powershell
python agents/m365-copilot/generate-icons.py
```

This requires Python with Pillow (PIL) installed.

## Limitations

- **Sideload requires admin approval** in many tenants — check with your IT admin
- **AppSource publication** requires passing Microsoft's Responsible AI and security review
- **Agent description limit** — declarative agent description is capped at 1000 chars; truncated in the UI
- **Conversation starters** — max 12, max ~80 chars per starter title for good UX
- **Plugin function limit** — when >5 plugins are configured for an agent, semantic matching kicks in. Within a plugin, there's no hard tool limit but >10 tools may degrade quality due to context window
- **Rate limiting** — Microsoft 365 Copilot infrastructure shares egress IPs across tenants. If the agent gets throttled (429), increase `MCP_ANON_RATE_LIMIT` (currently 100) or add Entra SSO auth for per-user rate limits

## Documentation Links

- [Declarative agents overview](https://learn.microsoft.com/microsoft-365-copilot/extensibility/overview-declarative-agent)
- [Build MCP plugins](https://learn.microsoft.com/microsoft-365-copilot/extensibility/build-mcp-plugins)
- [Plugin manifest schema (v2.4)](https://learn.microsoft.com/microsoft-365-copilot/extensibility/plugin-manifest-2.4)
- [Declarative agent manifest schema (v1.5)](https://learn.microsoft.com/microsoft-365-copilot/extensibility/declarative-agent-manifest-1.5)
- [Microsoft 365 Agents Toolkit](https://aka.ms/M365AgentsToolkit)
