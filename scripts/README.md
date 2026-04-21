# Scripts

Installation and configuration scripts for the OPC UA Knowledge Base MCP endpoints.

## Install Scripts

### `install-mcp.ps1` (PowerShell)

Configures MCP client applications (GitHub Copilot CLI, Claude Desktop) to use the OPC UA KB endpoints.

```powershell
# Hosted mode — uses the cloud-hosted MCP endpoints (recommended)
.\scripts\install-mcp.ps1 -Mode hosted -ApiKey <your-search-api-key>

# Local mode — uses the locally installed dotnet tool
.\scripts\install-mcp.ps1 -Mode local -ApiKey <your-search-api-key>
```

**What it does:**
1. Detects installed MCP clients (Copilot CLI, Claude Desktop)
2. Adds/updates `opcua-kb` (KB RAG endpoint) and `opcua-kb-tools` (custom MCP server) entries
3. In local mode, verifies the `opcua-kb-mcp` dotnet tool is installed

**Configuration files modified:**
- GitHub Copilot CLI: `~/.copilot/mcp.json`
- Claude Desktop: `~/AppData/Roaming/Claude/claude_desktop_config.json` (Windows) or `~/Library/Application Support/Claude/claude_desktop_config.json` (macOS)

### `install-mcp.sh` (Bash)

Bash equivalent of the PowerShell script:

```bash
SEARCH_API_KEY=<your-search-api-key> ./scripts/install-mcp.sh hosted
SEARCH_API_KEY=<your-search-api-key> ./scripts/install-mcp.sh local
```

## Manual Configuration

### GitHub Copilot CLI

Add to `~/.copilot/mcp.json`:

```json
{
  "mcpServers": {
    "opcua-kb": {
      "type": "http",
      "url": "https://<prefix>-search.search.windows.net/knowledgebases/<prefix>-kb/mcp?api-version=2025-11-01-preview",
      "headers": { "api-key": "<your-search-api-key>" }
    },
    "opcua-kb-tools": {
      "type": "http",
      "url": "https://<mcp-server-fqdn>/",
      "headers": { "api-key": "<your-search-api-key>" }
    }
  }
}
```

### Claude Desktop

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "opcua-kb-tools": {
      "command": "opcua-kb-mcp",
      "args": ["--stdio"],
      "env": {
        "SEARCH_ENDPOINT": "https://<prefix>-search.search.windows.net",
        "SEARCH_API_KEY": "<your-search-api-key>"
      }
    }
  }
}
```

### Local stdio mode (any MCP client)

```bash
# Install the dotnet tool globally
dotnet tool install -g OpcUaKb.McpServer

# Run with stdio transport
SEARCH_ENDPOINT=https://<prefix>-search.search.windows.net \
SEARCH_API_KEY=<key> \
opcua-kb-mcp --stdio
```

Or from source:

```bash
dotnet run --project src/OpcUaKb.McpServer -- --stdio
```
