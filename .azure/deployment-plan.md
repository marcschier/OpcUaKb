# Azure Deployment Plan

> **Status:** Deployed

Generated: 2026-07-10

---

## 1. Project Overview

**Goal:** Extend the existing production OPC UA Knowledge Base with a durable asynchronous workflow that maps a full live-server NodeSet2 AddressSpace into one or more instantiated official companion-spec projections, with generated NodeSet2 XML, JSON/CSV gateway mappings, reports, and private authenticated artifact delivery.

**Path:** Add Components

The application/infra implementation was approved in the preceding plan-mode review. This deployment plan records that approved architecture for Azure validation and deployment.

---

## 2. Requirements

| Attribute | Value |
|-----------|-------|
| Classification | Production |
| Scale | Medium; queue-scaled worker, 0–2 replicas, one job per replica |
| Budget | Balanced; scale to zero when idle |
| **Subscription** | Industrial-IoT-Edge (`53d910a7-f1f8-4b7a-8ee0-6e6b67bddd82`) |
| **Location** | `westus3` |
| Resource group | `rg-opcua-kb` |
| Storage authentication | Managed Identity only; shared key/public network remain disabled |
| Artifact access | Private blob references + API-key-gated MCP streaming endpoint |

---

## 3. Components Detected

| Component | Type | Technology | Path |
|-----------|------|------------|------|
| OPC UA KB MCP server | API | .NET 10 / Container Apps / MCP SDK | `src/OpcUaKb.McpServer` |
| Companion projection engine | Library | .NET 10 / streaming XML / Azure Search / Foundry | `src/OpcUaKb.Core` |
| Mapping worker | Worker | Same MCP image, `--mapping-worker` mode | `src/OpcUaKb.McpServer` |
| NodeSet/catalog pipeline | Batch job | .NET 10 / Container Apps Job | `src/OpcUaKb.Pipeline` |
| Azure infrastructure | IaC | Bicep | `infra/main.bicep` |

---

## 4. Recipe Selection

**Selected:** Bicep + Azure CLI

**Rationale:** This repository already uses a single resource-group-scope Bicep template and an idempotent `infra/deploy.sh` workflow. The change extends those existing resources and deploys the same application images; no AZD re-initialization or new project template is appropriate.

---

## 5. Architecture

**Stack:** Azure Container Apps workload-profile environment, Storage Queue/Blob, Azure AI Search, Azure AI Foundry, managed identities, private endpoints.

### Service Mapping

| Component | Azure Service | SKU / configuration |
|-----------|---------------|---------------------|
| MCP API | Container App | Existing Consumption profile, public HTTPS ingress |
| Mapping worker | Container App | New internal/no-ingress app, Consumption, 2 CPU/4 GiB, min 0/max 2 |
| Job dispatch | Storage Queue | New `model-mapping-jobs` queue in existing MI-only StorageV2 account |
| Inputs/artifacts/status | Blob Storage | Existing private `opcua-content` container |
| Candidate discovery | Azure AI Search | Existing Standard `opcua-content-index-v2` |
| Semantic ranking | Azure AI Foundry | Existing GPT-4o deployment, worker MI |
| Image registry | Azure Container Registry | Existing Basic ACR, MI-only pull |

### New networking

- Queue private endpoint in the existing private-endpoint subnet.
- `privatelink.queue.core.windows.net` private DNS zone, VNet link, and endpoint DNS zone group.
- Worker has no ingress and uses the existing VNet-integrated Container Apps environment.

### Security / RBAC

- Mapping worker system MI:
  - AcrPull on ACR
  - Storage Blob Data Contributor on storage
  - Storage Queue Data Contributor on storage
  - Search Index Data Reader on Search
  - Cognitive Services OpenAI User on Foundry
- MCP server MI:
  - existing Blob role
  - new Storage Queue Data Contributor to submit jobs
- KEDA queue scaler uses worker system MI; no storage connection string.
- Custom MCP application authentication uses independent secure `mcpAccessKey`, not the Search admin key.
- Search admin key remains an internal secret only until `SearchService` is migrated to MI.

---

## 6. Provisioning Limit Checklist

### Resource inventory and capacity

| Resource Type | Number to Deploy | Total After Deployment | Limit/Quota | Notes |
|---------------|------------------|------------------------|-------------|-------|
| `Microsoft.App/managedEnvironments` | 0 | 1 | 50 | `az quota list` for Microsoft.App/westus3: Managed Environment Count=50 |
| `Microsoft.App/containerApps` | 1 | 2 | Core-based environment capacity; no fixed app-count quota returned | Existing environment uses Consumption profile; worker max 2 replicas × 2 vCPU and scales to zero |
| `Microsoft.Storage/storageAccounts/queueServices/queues` | 1 | 1 new queue | Child resource in existing storage account | No new storage account quota/capacity required; queue messages contain only compact job IDs |
| `Microsoft.Network/privateEndpoints` | 1 | 2 | Existing subnet has capacity | New queue PE uses existing dedicated PE subnet |
| `Microsoft.Network/privateDnsZones` | 1 | 2 | Fixed service limits well above total | New queue private DNS zone only |
| Foundry model deployments | 0 | unchanged | Existing deployed capacity | No new TPM deployment/quota requested |
| Azure AI Search services | 0 | unchanged | Existing Standard service | Adds fields/documents to existing index only |

**Status:** ✅ Required deployment capacity is available. The only provider quota surfaced by Azure Quota CLI for these changes is managed-environment count (1/50 after deployment). No new environment or model deployment is created.

---

## 7. Execution Checklist

### Phase 1: Planning
- [x] Analyze workspace
- [x] Gather requirements
- [x] Confirm subscription and location
- [x] Prepare resource inventory
- [x] Fetch relevant quotas and validate capacity
- [x] Scan codebase
- [x] Select Bicep/Azure CLI recipe
- [x] Plan architecture
- [x] User approved the implementation plan

### Phase 2: Execution
- [x] Research components and pairing constraints
- [x] Generate application and infrastructure changes
- [x] Apply MI-only security and private networking
- [x] Generate/update deployment script and documentation
- [x] Run functional local parser/engine/artifact/auth tests
- [x] Set status to Ready for Validation

### Phase 3: Validation
- [x] **PREREQUISITE:** Plan status is Ready for Validation
- [x] Invoke azure-validate skill
- [x] All validation checks pass
  - [x] Bicep compilation
  - [x] Resource-group template validation
  - [x] What-if preview
  - [x] Azure authentication/context
  - [x] Azure policy compatibility
  - [x] Full .NET solution build
  - [x] Mapping regression tests
  - [x] Static RBAC verification
- [x] Update plan status to Validated
- [x] Record validation proof below

### Phase 4: Deployment
- [x] Invoke azure-deploy skill
- [x] Infrastructure deployment successful
- [x] Rebuild/deploy pipeline and MCP/worker images (4.4.1)
- [x] Run pipeline to populate exact model fields/catalog
- [x] Submit and complete realistic mapping jobs
- [x] Validate generated NodeSet and artifact hashes
- [x] Report endpoints/artifacts
- [x] Update plan status to Deployed

---

## 8. Validation Proof

| Check | Command Run | Result | Timestamp |
|-------|-------------|--------|-----------|
| Local solution build | `dotnet build OpcUaKnowledgeBase.slnx` | ✅ Pass; only pre-existing HostedAgent NU1902 warnings | 2026-07-10 |
| Mapping regression tests | `dotnet run --project src/OpcUaKb.Test -- mapping` | ✅ Parser/filter, nested multi-projection engine, exact models/QNames/references/artifacts pass | 2026-07-10 |
| Bicep compilation | `az bicep build --file infra/main.bicep` | ✅ Pass; two pre-existing warnings | 2026-07-10 |
| Deploy script syntax | `bash -n infra/deploy.sh` | ✅ Pass | 2026-07-10 |
| MCP auth smoke | local `tools/list` + create call | ✅ 17 tools; mapping submission 401 invalid/no key, 503 no configured key | 2026-07-10 |
| Azure context | `az account show` | ✅ Industrial-IoT-Edge / correct tenant | 2026-07-10 |
| Container Apps quota | `az quota list --scope .../Microsoft.App/locations/westus3` | ✅ Managed Environment Count limit 50, current total 1 | 2026-07-10 |
| Azure template validation | `az deployment group validate -g rg-opcua-kb --template-file infra/main.bicep --parameters @<secure-temp>` | ✅ `Succeeded` | 2026-07-10 |
| Azure what-if | `az deployment group what-if ... --result-format ResourceIdOnly` | ✅ 12 creates, existing resources deploy/update, 0 deletes | 2026-07-10 |
| Static RBAC | Bicep role review + `az role definition list --name ...` | ✅ Worker/MCP roles use exact built-in GUIDs at resource scopes | 2026-07-10 |
| Azure policy | Template validation + `az policy assignment list` | ✅ Existing subnet default-outbound deny remains satisfied; no policy denial | 2026-07-10 |
| Infrastructure deployment | `az deployment group create` + idempotent retry | ✅ Queue/PE/DNS/worker/RBAC/application key deployed; retry succeeded in 90s | 2026-07-10 |
| Application images | `az acr build` + Container App/Job updates | ✅ MCP/worker and pipeline 4.4.1 deployed | 2026-07-10 |
| Live resource/RBAC verification | `az containerapp show`, PE/queue lookup, `az role assignment list` | ✅ Worker healthy, no ingress, queue PE approved, all five worker roles + MCP queue role present | 2026-07-10 |
| Definitive pipeline refresh | Container Apps Job `opcua-kb-pipeline-job-ytp2lcd` | ✅ 60,261/60,261 NodeSet docs; 67 models/63 latest; failed sources merged; distinct version IDs/ranks | 2026-07-10 |
| Proposed mapping job | `cp-39759532e4fa00acc1823badc2cfa50c7723e1a9` | ✅ Completed attempt 1; 4 proposed projections; 6 hash-verified artifacts | 2026-07-10 |
| Accepted mapping job | `cp-d58fd000e2a9cfb7b1b05a087ee1f6bafb8e46bc` | ✅ Completed attempt 1; 4 accepted DEXPI projections; generated NodeSet validation 0 errors | 2026-07-10 |
| Closure worker job | `cp-e76ba4e020a38a9476fc2a85516a46b5225f556f` | ✅ Final deployed worker completed attempt 1; 4 projections, 0 errors, 6 artifacts | 2026-07-10 |
| Artifact integrity | authenticated `/mapping-artifacts/...` downloads + SHA-256 | ✅ XML/JSON/CSV/reports/ZIP hashes and ZIP names match status metadata | 2026-07-10 |

**Validated by:** azure-validate workflow
**Validation timestamp:** 2026-07-10

---

## 9. Files Generated / Updated

| File | Purpose | Status |
|------|---------|--------|
| `.azure/deployment-plan.md` | Azure deployment source of truth | ✅ |
| `infra/main.bicep` / `infra/main.json` | Queue/PE/DNS/worker/RBAC/application key | ✅ |
| `infra/deploy.sh` | Builds and deploys pipeline, MCP server, and worker images | ✅ |
| `src/OpcUaKb.Core/*Companion*` | Parser, repository, candidate reasoning, projection engine, artifacts, job workflow | ✅ |
| `src/OpcUaKb.Core/Tools/*CompanionProjection*` | MCP start/status tools | ✅ |
| `src/OpcUaKb.McpServer/Program.cs` | HTTP auth/download endpoint and worker mode | ✅ |
| `src/OpcUaKb.Pipeline/NodeSetParser.cs` | Exact model identifiers + version ranking | ✅ |
| `src/OpcUaKb.Pipeline/NodeSetModelCatalog.cs` | Exact official model catalog | ✅ |
| `src/OpcUaKb.Test/testdata/mapping-*.xml` | Deterministic mapping fixtures | ✅ |

---

## 10. Next Steps

> Current: Deployed

1. Use `create_companion_projection` with a real live-server NodeSet export.
2. Review proposed mappings at the default confidence thresholds.
3. Load accepted `projection.nodeset2.xml` + `mapping.json` into the gateway.
