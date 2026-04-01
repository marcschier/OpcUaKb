# OPC UA Knowledge Base MCP Server

An Azure AI Search agentic retrieval pipeline that exposes OPC UA reference specifications as an MCP (Model Context Protocol) endpoint for AI agents.

## Architecture

- **Web Knowledge Source** — Bing Custom Search confined to `reference.opcfoundation.org` for real-time queries
- **Crawl + Index Pipeline** — Downloads all content, extracts text/images/tables, creates vector embeddings, indexes in Azure AI Search
- **Knowledge Base** — Orchestrates retrieval from both sources with GPT-4o for query planning and answer synthesis
- **MCP Endpoint** — Automatically exposed by the knowledge base for any MCP-compatible client

## Projects

| Project | Description |
|---------|-------------|
| `OpcUaKb.Setup` | Creates the Web Knowledge Source, Knowledge Base, and verifies the MCP endpoint |
| `OpcUaKb.Crawler` | Crawls `reference.opcfoundation.org` and stores content in Azure Blob Storage |
| `OpcUaKb.Indexer` | Processes crawled HTML, chunks text, generates embeddings, pushes to AI Search |
| `OpcUaKb.Test` | Runs verification queries against the knowledge base |

## Azure Resources

| Resource | Name | Purpose |
|----------|------|---------|
| Resource Group | `rg-opcua-kb` | Container for all resources |
| AI Search | `opcua-kb-search` | Search index + knowledge base + MCP endpoint |
| Azure OpenAI | `opcua-kb-openai` | GPT-4o (query planning) + text-embedding-3-large |
| Blob Storage | `opcuakbstorage` | Crawled content storage |
| Document Intelligence | `opcua-kb-docai` | Rich content extraction (tables, images) |

## Quick Start

```bash
# Set environment variables
export SEARCH_API_KEY="<your-search-admin-key>"
export STORAGE_CONNECTION_STRING="<your-storage-connection-string>"
export AOAI_ENDPOINT="https://opcua-kb-openai.openai.azure.com"
export AOAI_API_KEY="<your-openai-key>"

# 1. Create knowledge source + knowledge base
dotnet run --project src/OpcUaKb.Setup

# 2. Crawl OPC UA reference specs
dotnet run --project src/OpcUaKb.Crawler

# 3. Index crawled content
dotnet run --project src/OpcUaKb.Indexer

# 4. Run verification tests
dotnet run --project src/OpcUaKb.Test
```

## MCP Endpoint

```
https://opcua-kb-search.search.windows.net/knowledgebases/opcua-kb/mcp?api-version=2025-11-01-preview
```

Configure in `~/.copilot/mcp.json`:
```json
{
  "mcpServers": {
    "opcua-kb": {
      "type": "http",
      "url": "https://opcua-kb-search.search.windows.net/knowledgebases/opcua-kb/mcp?api-version=2025-11-01-preview",
      "headers": {
        "api-key": "<your-search-api-key>"
      }
    }
  }
}
```
