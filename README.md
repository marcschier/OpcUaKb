# OPC UA Knowledge Base MCP Server

<img src="docs/images/logo.svg" alt="OPC UA Knowledge Base" width="80" align="left" style="margin-right: 16px"/>

[![Build](https://github.com/marcschier/OpcUaKb/actions/workflows/ci.yml/badge.svg)](https://github.com/marcschier/OpcUaKb/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![MCP](https://img.shields.io/badge/MCP-1.2-green)](https://modelcontextprotocol.io)
[![Version](https://img.shields.io/badge/version-3.0-orange)](version.json)

<br clear="left"/>

An Azure AI Search agentic retrieval pipeline that exposes the complete OPC UA reference specifications as MCP endpoints for AI agents. Uses Azure AI Foundry with Managed Identity for keyless authentication.

## ✨ Key Features

- 🌐 **180K+ indexed documents** from `*.opcfoundation.org` — spec text, tables, diagrams, NodeSet XMLs
- 🔧 **10 MCP tools** — structured search, compliance validation, version comparison, model design suggestions
- 🧬 **Type hierarchy resolution** — cross-file ObjectType inheritance with declared vs inherited member counting
- 📊 **Version-aware indexing** — `is_latest` / `version_rank` tags, automatic fallback to older versions
- ☁️ **UA-CloudLibrary integration** — 450+ NodeSets with popularity ranking and cross-source version comparison
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

### Version Filtering

All search tools default to the **latest spec version** with automatic fallback to older versions if too few results:

| Parameter | Values | Effect |
|-----------|--------|--------|
| `version_mode` | `latest` (default) | Only current version |
| | `previous` | One version before latest |
| | `oldest` | Earliest available version |
| | `all` | Search across all versions |
| `spec_version` | `v104`, `v105`, `v200`, etc. | Specific version (overrides `version_mode`) |

## 🚀 Deploy

```bash
./infra/deploy.sh -s <subscription-id> -g rg-opcua-kb -p opcua-kb -l eastus
```

The script is idempotent. See [`infra/README.md`](infra/README.md) for full resource details, Bicep structure, and monitoring.

## 📦 Quick Install

```bash
# Hosted mode (recommended) — configures Copilot CLI + Claude Desktop
.\scripts\install-mcp.ps1 -Mode hosted -ApiKey <your-search-api-key>

# Or install as local dotnet tool
dotnet tool install -g OpcUaKb.McpServer
.\scripts\install-mcp.ps1 -Mode local -ApiKey <your-search-api-key>
```

See [`scripts/README.md`](scripts/README.md) for manual configuration and all client setup options.

## 🔗 MCP Endpoints

### Azure AI Search KB (RAG with answer synthesis)

```
https://<prefix>-search.search.windows.net/knowledgebases/<prefix>-kb/mcp?api-version=2025-11-01-preview
```

### Custom MCP Server (10 structured tools)

```
https://<mcp-server-fqdn>/
```

| Tier | Identification | Default Limit |
|------|---------------|---------------|
| Authenticated | Valid `api-key` header | Unlimited |
| Anonymous | No key (per IP) | 10 req/min |
| Blocked | `MCP_REQUIRE_AUTH=true` | 401 Unauthorized |

## 💬 Interactive Chatbot

```bash
export SEARCH_API_KEY="$(az search admin-key show --service-name <prefix>-search -g <rg> --query primaryKey -o tsv)"
az login  # Keyless AOAI auth via DefaultAzureCredential
dotnet run --project src/OpcUaKb.Chat
```

## ⚙️ Pipeline

Weekly crawl + index pipeline (Sunday 2am UTC, Container Apps Job, 24h timeout):

| Phase | Description |
|-------|-------------|
| **Crawl** | BFS crawl of `*.opcfoundation.org`. Incremental with state tracking. |
| **Index** | HTML → chunks → embeddings (`text-embedding-3-large`) → Azure AI Search. |
| **NodeSet** | Parse XMLs, build type hierarchy, generate summaries + hierarchy docs. |
| **CloudLib** *(optional)* | Download 450+ NodeSets from [UA-CloudLibrary](https://uacloudlibrary.opcfoundation.org), index as `cloudlib_*` content types. |

See [`src/README.md`](src/README.md) for running locally, project details, and search index schema.
