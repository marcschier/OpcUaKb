# Copilot Instructions for OPC UA Knowledge Base

## Build & Run

```bash
# Build everything
dotnet build

# Run the pipeline locally (requires env vars — see README.md "Manual Pipeline Run")
dotnet run --project src/OpcUaKb.Pipeline

# Run the chatbot (requires SEARCH_API_KEY and AOAI_API_KEY)
dotnet run --project src/OpcUaKb.Chat

# Validate Bicep infrastructure
az bicep build --file infra/main.bicep

# Deploy everything (idempotent)
./infra/deploy.sh -s <subscription-id> -g rg-opcua-kb -p opcua-kb -l eastus
```

There are no unit tests — `OpcUaKb.Test` is a console app requiring live Azure credentials. CI validates compilation only.

## Architecture

- **Single solution** (`OpcUaKnowledgeBase.slnx`) with 6 projects under `src/`
- **Pipeline** (`OpcUaKb.Pipeline`): Top-level statements, sealed classes, no explicit namespaces. Three phases: crawl → index → nodeset. Runs as Azure Container Apps Job on a weekly cron.
- **Infrastructure**: `infra/main.bicep` (all Azure resources) + `infra/deploy.sh` (end-to-end deployment script using `az rest` for preview APIs)
- **Index**: Azure AI Search `opcua-content-index` with `content_type` field distinguishing `html` vs `nodeset` docs
- **API version**: Azure AI Search agentic retrieval uses `2025-11-01-preview`. Knowledge sources use `kind: "web"` with `webParameters.domains.allowedDomains`.

## Azure Resource Configuration

These are the **production values** — do not revert to lower defaults:

| Parameter | Value | Notes |
|-----------|-------|-------|
| AI Search SKU | `standard` | Required for semantic ranker + knowledge bases |
| Embedding model capacity | `120` (TPM in thousands) | Upgraded from 30 to avoid 429 throttling |
| GPT-4o capacity | `30` | |
| Container Apps Job timeout | `86400` (24 hours) | Full crawl + index takes ~17 hours |
| Cron schedule | `0 2 * * 0` | Weekly Sunday 2am UTC |
| Resource group | `rg-opcua-kb` | Region: eastus |

## HttpClient Usage — Critical Pattern

**Never create `new HttpClient()` inside loops or retry lambdas.** This causes socket exhaustion and loses auth headers. Always use a shared instance:

```csharp
// CORRECT — shared client with default headers
var http = new HttpClient();
http.DefaultRequestHeaders.Add("api-key", apiKey);
// Pass http to methods, reuse across all calls

// WRONG — causes socket exhaustion, silent auth failures
for (var i = 0; i < batches.Count; i++)
{
    var client = new HttpClient(); // ← never do this
    await client.PostAsync(url, content);
}
```

This pattern caused 3 consecutive pipeline runs (each ~17 hours) to report success while producing 0 NodeSet documents.

## Error Handling in Batch Operations

**Never swallow HTTP errors in batch processing loops.** If `EnsureSuccessStatusCode()` throws inside a `try/catch` that only logs, the entire batch silently fails while the pipeline reports success.

Pattern to follow:
- Use `RetryHelper.RetrySearchAsync()` for Azure Search SDK calls
- Use `RetryHelper.RetryAsync()` for raw HTTP calls — handles 429/503 with `Retry-After`
- Log failures with `[PHASE] Phase=X Error=Y` structured format for dashboard visibility
- Track upload counts and compare against expected totals at phase end

## Azure AI Search Agentic Retrieval API

The deploy script uses `az rest` for preview API operations. Key schema patterns:

```bash
# Knowledge source (web type)
az rest --method PUT \
  --url "https://{search}.search.windows.net/knowledgebases/{kb}/knowledgesources/{ks}?api-version=2025-11-01-preview" \
  --body '{
    "kind": "web",
    "webParameters": { "domains": { "allowedDomains": ["*.opcfoundation.org"] } }
  }'

# Knowledge base with models
az rest --method PUT \
  --url "https://{search}.search.windows.net/knowledgebases/{kb}?api-version=2025-11-01-preview" \
  --body '{
    "models": [{ "kind": "azureOpenAI", "azureOpenAIParameters": { ... } }]
  }'
```

The MCP endpoint is automatically exposed at:
```
https://{search}.search.windows.net/knowledgebases/{kb}/mcp?api-version=2025-11-01-preview
```

## Conventions

- .NET 10, nullable enabled, implicit usings
- Top-level statements for all console apps (Pipeline, Chat, Setup, Test)
- Sealed classes preferred
- No explicit namespaces in Pipeline project
- NuGet: `Azure.Search.Documents` 11.8.0-beta.1, `Azure.AI.OpenAI` 2.9.0-beta.1
- Structured logging: `[PHASE] Key=Value` format for KQL dashboard queries
- Pipeline status tracked in `_pipeline-status.json` blob
