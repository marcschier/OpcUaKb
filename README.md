# OPC UA Knowledge Base MCP Server

<p align="center">
  <img src="docs/images/hero-banner.svg" alt="OPC UA Knowledge Base" width="100%"/>
</p>

[![Build](https://github.com/marcschier/OpcUaKb/actions/workflows/ci.yml/badge.svg)](https://github.com/marcschier/OpcUaKb/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![MCP](https://img.shields.io/badge/MCP-1.2-green)](https://modelcontextprotocol.io)
[![Version](https://img.shields.io/badge/version-3.0-orange)](version.json)

An Azure AI Search agentic retrieval pipeline that exposes the complete OPC UA reference specifications as MCP (Model Context Protocol) endpoints for AI agents. Uses Azure AI Foundry with Managed Identity for keyless authentication.

## ✨ Key Features

- 🌐 **180K+ indexed documents** from `*.opcfoundation.org` — spec text, tables, diagrams, NodeSet XMLs
- 🔧 **10 MCP tools** — structured search, compliance validation, version comparison, model design suggestions
- 🧬 **Type hierarchy resolution** — cross-file ObjectType inheritance with declared vs inherited member counting
- 📊 **Version-aware indexing** — `is_latest` / `version_rank` tags, automatic fallback to older versions
- ☁️ **UA-CloudLibrary integration** — 63 NodeSets with popularity ranking and cross-source version comparison
- 🧠 **RAG knowledge base** — Azure AI Foundry + GPT-4o for query planning and answer synthesis
- 🔒 **Managed Identity auth** — keyless AOAI access throughout the pipeline and MCP server
- 📈 **Popularity-boosted ranking** — widely-adopted specs surface first via logarithmic download-count scoring

## 🏗️ Architecture

<p align="center">
  <img src="docs/images/architecture.svg" alt="Architecture" width="100%"/>
</p>

## 🔌 MCP Tools

The custom MCP server exposes 10 tools alongside the Azure AI Search KB endpoint:

### 🔍 Search & Discovery

<table>
<tr>
<td width="50%" valign="top">

**`search_nodes`** — Structured search with OData filters by node class, spec, parent type, modelling rule, and `source`. Version-aware with two-pass fallback.

</td>
<td width="50%" valign="top">

**`search_docs`** — Full-text search across HTML specification pages, tables, and diagrams. Version-aware.

</td>
</tr>
<tr>
<td valign="top">

**`get_type_hierarchy`** — ObjectType inheritance chain with declared/inherited member counts and supertype chain.

</td>
<td valign="top">

**`get_spec_summary`** — Pre-computed per-spec or cross-spec NodeSet statistics (node counts, top ObjectTypes). Filterable by `source`.

</td>
</tr>
<tr>
<td valign="top">

**`count_nodes`** — Faceted aggregation by node_class, spec_part, modelling_rule, data_type, or `source`.

</td>
<td valign="top">

**`list_specs`** — Ranked catalog with version, node count, popularity, and cross-source version comparison. Use `unique_to_source=true` to find CloudLib NodeSets not in the official index or with different versions.

</td>
</tr>
</table>

### 🛡️ Compliance & Modelling

<table>
<tr>
<td width="50%" valign="top">

**`validate_nodeset`** — Validate NodeSet XML against OPC UA standard and OPC 11030 best practices — checks naming conventions, modelling rules, type hierarchy, reference types.

</td>
<td width="50%" valign="top">

**`compare_versions`** — Compare two versions of a companion spec, classify changes as backward-compatible or breaking per OPC 11030 §3.

</td>
</tr>
<tr>
<td valign="top">

**`check_compliance`** — Check a NodeSet implementation against a companion spec — finds missing mandatory/optional nodes, data type mismatches.

</td>
<td valign="top">

**`suggest_model`** — Suggest OPC UA information model design based on a domain description, recommending base types from DI/Machinery/IA and OPC 11030 best practices.

</td>
</tr>
</table>

### 🕐 Version Filtering

All search tools default to the **latest spec version** with automatic fallback to older versions if too few results:

| Parameter | Values | Effect |
|-----------|--------|--------|
| `version_mode` | `latest` (default) | Only current version |
| | `previous` | One version before latest |
| | `oldest` | Earliest available version |
| | `all` | Search across all versions |
| `spec_version` | `v104`, `v105`, `v200`, etc. | Specific version (overrides `version_mode`) |

## 🚀 Deploy

### One-command deployment

```bash
./infra/deploy.sh \
  -s <subscription-id> \
  -g rg-opcua-kb \
  -p opcua-kb \
  -l eastus
```

| Flag | Description | Default |
|------|-------------|---------|
| `-s, --subscription` | Azure subscription ID | (required) |
| `-g, --resource-group` | Resource group name | `rg-opcua-kb` |
| `-p, --prefix` | Resource name prefix | `opcua-kb` |
| `-l, --location` | Azure region | `eastus` |

Prerequisites: `az` CLI (logged in), `docker`, `dotnet` SDK 10.0+, `nbgv`.
The script is idempotent — safe to run multiple times.

### ☁️ Azure Resources

All resources are defined in `infra/main.bicep`:

| Resource | Derived Name | Purpose |
|----------|-------------|---------|
| AI Search (Standard) | `{prefix}-search` | Search index + knowledge base + MCP endpoint |
| Azure AI Foundry | `{prefix}-foundry` | AIServices account + default project; GPT-4o + text-embedding-3-large. MI auth. |
| Blob Storage | `{prefix}storage` | Crawled content storage |
| Container Registry | `{prefix}registry` | Pipeline + MCP server Docker images |
| Container Apps Job | `{prefix}-pipeline-job` | Weekly crawl + index (cron: `0 2 * * 0`, 24h timeout). MI auth. |
| Container App | `{prefix}-mcp-server` | Hosted MCP server (scale 0–2, HTTP auto-scale) |

## 📦 Quick Install

### One-command setup (hosted — recommended)

```bash
# PowerShell
.\scripts\install-mcp.ps1 -Mode hosted -ApiKey <your-search-api-key>

# Bash
SEARCH_API_KEY=<your-search-api-key> ./scripts/install-mcp.sh hosted
```

This configures both GitHub Copilot CLI and Claude Desktop (if installed) to use the hosted MCP endpoints. No local install needed.

### Install as dotnet tool (local stdio)

```bash
# Install the tool globally
dotnet tool install -g OpcUaKb.McpServer

# Configure clients for local mode
.\scripts\install-mcp.ps1 -Mode local -ApiKey <your-search-api-key>
```

The tool runs as `opcua-kb-mcp --stdio` and communicates over stdin/stdout.

## 🔗 MCP Endpoints

### Azure AI Search KB (RAG with answer synthesis)

```
https://<prefix>-search.search.windows.net/knowledgebases/<prefix>-kb/mcp?api-version=2025-11-01-preview
```

### Custom MCP Server (10 structured tools)

Hosted on Azure Container Apps with scale-to-zero and configurable rate limiting.

```
https://<mcp-server-fqdn>/
```

**Access tiers:**

| Tier | Identification | Default Limit | Behavior |
|------|---------------|---------------|----------|
| Authenticated | Valid `api-key` header | Unlimited | Full access, prioritized |
| Anonymous | No key (per IP) | 10 req/min | Rate-limited, 429 if exceeded |
| Blocked | No key + `MCP_REQUIRE_AUTH=true` | Rejected | 401 Unauthorized |

**Rate limiting configuration (env vars):**

| Variable | Default | Description |
|----------|---------|-------------|
| `MCP_API_KEY` | from `SEARCH_API_KEY` | API key for authenticated access |
| `MCP_REQUIRE_AUTH` | `false` | Set `true` to block all anonymous requests |
| `MCP_ANON_RATE_LIMIT` | `10` | Max requests/min for anonymous callers (per IP) |
| `MCP_AUTH_RATE_LIMIT` | `0` | Max requests/min for authenticated callers (0 = unlimited) |

### 🔧 Manual Configuration: GitHub Copilot CLI

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

### 🔧 Manual Configuration: Claude Desktop

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

## 💬 Interactive Chatbot

```bash
export SEARCH_API_KEY="$(az search admin-key show --service-name <prefix>-search -g <rg> --query primaryKey -o tsv)"
# Auth to AOAI is keyless via DefaultAzureCredential — `az login` first and
# ensure your user has the `Cognitive Services OpenAI User` role on the
# Azure AI Foundry account (`<prefix>-foundry`).
dotnet run --project src/OpcUaKb.Chat
```

## ⚙️ Pipeline

The pipeline runs weekly (Sunday 2am UTC) as a Container Apps Job with a 24-hour timeout:

| Phase | Description |
|-------|-------------|
| **1. Crawl** | BFS crawl of `reference.opcfoundation.org` + `profiles.opcfoundation.org` + other `*.opcfoundation.org` subdomains. Incremental with state tracking. |
| **2. Index** | Parse HTML → chunks, generate embeddings via `text-embedding-3-large` (120K TPM), upload to Azure AI Search. Version catalog built from crawled main page. |
| **3. NodeSet** | Parse NodeSet XMLs, build cross-file type hierarchy, generate per-ObjectType hierarchy docs + per-spec summaries. |
| **4. CloudLibrary** *(optional)* | If `CLOUDLIB_USERNAME` + `CLOUDLIB_PASSWORD` set, download all NodeSets + REST metadata from [UA-CloudLibrary](https://uacloudlibrary.opcfoundation.org), parse and index separately as `cloudlib_*` content types. Docs tagged with `source`, `namespace_uri`, `publication_date`, `popularity`, and `in_opcfoundation_index`. |

### ▶️ Run manually

```bash
# Required
export STORAGE_CONNECTION_STRING="$(az storage account show-connection-string --name <prefix>storage -g <rg> -o tsv)"
export SEARCH_ENDPOINT="https://<prefix>-search.search.windows.net"
export SEARCH_API_KEY="$(az search admin-key show --service-name <prefix>-search -g <rg> --query primaryKey -o tsv)"
export AOAI_ENDPOINT="https://<prefix>-foundry.openai.azure.com"
# Auth to AOAI is keyless via DefaultAzureCredential — `az login` first and
# ensure your user has the `Cognitive Services OpenAI User` role on the
# Azure AI Foundry account (`<prefix>-foundry`).

# Optional: UA-CloudLibrary integration
export CLOUDLIB_USERNAME="your-email@example.com"
export CLOUDLIB_PASSWORD="your-password"

# Run locally
dotnet run --project src/OpcUaKb.Pipeline

# Or trigger the cloud job
az containerapp job start --name <prefix>-pipeline-job --resource-group <rg>
```

## 📁 Projects

| Project | Description |
|---------|-------------|
| `OpcUaKb.Pipeline` | Combined crawl + index + NodeSet parse pipeline (runs as Container Apps Job) |
| `OpcUaKb.McpServer` | Custom MCP server with 10 tools — search, compliance, modelling (HTTP + stdio) |
| `OpcUaKb.Chat` | Interactive console chatbot grounded by the knowledge base |
| `OpcUaKb.Setup` | Creates the Web Knowledge Source, Knowledge Base, and verifies the MCP endpoint |
| `OpcUaKb.Crawler` | Standalone crawler for `*.opcfoundation.org` |
| `OpcUaKb.Indexer` | Standalone HTML chunker + embedder + search indexer |
| `OpcUaKb.Test` | Runs verification queries against the knowledge base |

## 🔬 Search Index Reference

### Content Types

| Type | Description |
|------|-------------|
| `text`, `table`, `diagram` | HTML spec pages (text chunks, tables, diagrams) |
| `nodeset` | Individual NodeSet nodes from standard specs |
| `nodeset_summary` | Per-spec + master aggregation docs |
| `nodeset_hierarchy` | Per-ObjectType docs with supertype chain and member counts |
| `cloudlib_nodeset` | NodeSet nodes from UA-CloudLibrary (optional) |
| `cloudlib_summary` | CloudLibrary per-spec aggregation docs (optional) |
| `cloudlib_hierarchy` | CloudLibrary per-ObjectType hierarchy docs (optional) |

### Index Fields

| Field | Type | Filterable | Facetable | Description |
|-------|------|-----------|-----------|-------------|
| `browse_name` | String | ✓ | | Node browse name |
| `node_class` | String | ✓ | ✓ | ObjectType, Variable, Method, DataType, etc. |
| `spec_part` | String | ✓ | ✓ | Companion spec name (DI, Pumps, Part3, etc.) |
| `spec_version` | String | ✓ | | Version path segment (v104, v105, v200) |
| `parent_type` | String | ✓ | | Parent ObjectType browse name |
| `modelling_rule` | String | ✓ | ✓ | Mandatory, Optional, MandatoryPlaceholder, etc. |
| `data_type` | String | ✓ | ✓ | OPC UA data type |
| `content_type` | String | ✓ | | nodeset, nodeset_summary, nodeset_hierarchy, cloudlib_*, text, table, diagram |
| `is_latest` | Boolean | ✓ | | `true` for the latest version of each spec |
| `version_rank` | Int32 | ✓ | | 1 = latest, 2 = previous, 3 = older, etc. |
| `source` | String | ✓ | ✓ | `opcfoundation` or `cloudlib` |
| `namespace_uri` | String | ✓ | | OPC UA namespace URI (from ModelUri) |
| `publication_date` | DateTimeOffset | ✓ | | CloudLib publication date |
| `popularity` | Int64 | ✓ | | Download count; drives default scoring profile |
| `in_opcfoundation_index` | Boolean | ✓ | ✓ | `true` if namespace is also in the crawled opcfoundation index |
| `title`, `description` | String | | | CloudLib metadata |

## 🔄 CI/CD

GitHub Actions workflow (`.github/workflows/ci.yml`):
- **Push/PR to main** — build + compile all projects (full git history for NBGV)
- **Push to main** — build both Docker images (pipeline + MCP server), push to GHCR with SemVer tags

Container image tags: `<version>` (e.g., `3.0.0`) + `latest` + `<sha>`

## 📊 Monitoring

The Azure Monitor Workbook "OPC UA Pipeline Dashboard" provides:
- Pipeline phase transitions and execution history
- Crawl progress (downloaded/queued/errors over time)
- Index progress (chunks/embedded/uploaded)
- Errors and warnings table
- Execution duration bar chart

Access via: **Azure Portal → Monitor → Workbooks → "OPC UA Pipeline Dashboard"**

## 📋 Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (logged in)
- [Docker](https://docs.docker.com/get-docker/) (for container builds)
- [nbgv](https://github.com/dotnet/Nerdbank.GitVersioning) (`dotnet tool install -g nbgv`)
