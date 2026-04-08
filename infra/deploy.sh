#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
# OPC UA Knowledge Base — Azure Deployment Script
# Deploys all infrastructure, builds the pipeline image, and configures
# the knowledge base with MCP endpoint for Copilot CLI.
# ═══════════════════════════════════════════════════════════════════════
set -euo pipefail

# ── Colors ────────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

info()  { echo -e "${BLUE}[INFO]${NC}  $*"; }
ok()    { echo -e "${GREEN}[OK]${NC}    $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
fail()  { echo -e "${RED}[FAIL]${NC}  $*"; exit 1; }

# ── Defaults ──────────────────────────────────────────────────────────
SUBSCRIPTION=""
RESOURCE_GROUP="rg-opcua-kb"
PREFIX="opcua-kb"
LOCATION="eastus"

# ── Parse arguments ───────────────────────────────────────────────────
usage() {
  cat <<EOF
Usage: $(basename "$0") [OPTIONS]

Deploys the OPC UA Knowledge Base to Azure.

Options:
  -s, --subscription    Azure subscription ID (required)
  -g, --resource-group  Resource group name (default: ${RESOURCE_GROUP})
  -p, --prefix          Resource name prefix (default: ${PREFIX})
  -l, --location        Azure region (default: ${LOCATION})
  -h, --help            Show this help message
EOF
  exit 0
}

while [[ $# -gt 0 ]]; do
  case $1 in
    -s|--subscription)    SUBSCRIPTION="$2"; shift 2 ;;
    -g|--resource-group)  RESOURCE_GROUP="$2"; shift 2 ;;
    -p|--prefix)          PREFIX="$2"; shift 2 ;;
    -l|--location)        LOCATION="$2"; shift 2 ;;
    -h|--help)            usage ;;
    *) fail "Unknown option: $1. Use --help for usage." ;;
  esac
done

[[ -z "$SUBSCRIPTION" ]] && fail "Azure subscription ID is required. Use -s or --subscription."

# ── Derived names ─────────────────────────────────────────────────────
SEARCH_NAME="${PREFIX}-search"
ACR_NAME="${PREFIX//\-/}registry"
JOB_NAME="${PREFIX}-pipeline-job"
KB_NAME="${PREFIX}-kb"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

# ── Step 0: Validate prerequisites ───────────────────────────────────
info "Checking prerequisites..."

command -v az     >/dev/null 2>&1 || fail "Azure CLI (az) is not installed. Install from https://aka.ms/install-azure-cli"
command -v docker >/dev/null 2>&1 || fail "Docker is not installed. Install from https://docs.docker.com/get-docker/"
command -v dotnet >/dev/null 2>&1 || fail ".NET SDK (dotnet) is not installed. Install from https://dot.net"

ok "All prerequisites found."

# ── Step 1: Set Azure subscription ───────────────────────────────────
info "Setting Azure subscription to ${SUBSCRIPTION}..."
az account set --subscription "$SUBSCRIPTION"
ok "Subscription set."

# ── Step 2: Create resource group ────────────────────────────────────
info "Ensuring resource group '${RESOURCE_GROUP}' exists in '${LOCATION}'..."
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --output none 2>/dev/null || true
ok "Resource group ready."

# ── Step 3: Deploy Bicep template ────────────────────────────────────
info "Deploying Bicep template (this may take several minutes)..."
DEPLOY_OUTPUT=$(az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "${SCRIPT_DIR}/main.bicep" \
  --parameters prefix="$PREFIX" location="$LOCATION" \
  --query 'properties.outputs' \
  --output json) || fail "Bicep deployment failed. Check the Azure portal for details."

SEARCH_ENDPOINT=$(echo "$DEPLOY_OUTPUT" | jq -r '.searchEndpoint.value')
SEARCH_API_KEY=$(echo "$DEPLOY_OUTPUT" | jq -r '.searchApiKey.value')
AOAI_ENDPOINT=$(echo "$DEPLOY_OUTPUT" | jq -r '.aoaiEndpoint.value')
AOAI_API_KEY=$(echo "$DEPLOY_OUTPUT" | jq -r '.aoaiApiKey.value')
STORAGE_CONN_STR=$(echo "$DEPLOY_OUTPUT" | jq -r '.storageConnectionString.value')
ACR_LOGIN_SERVER=$(echo "$DEPLOY_OUTPUT" | jq -r '.acrLoginServer.value')
MCP_ENDPOINT=$(echo "$DEPLOY_OUTPUT" | jq -r '.mcpEndpoint.value')

ok "Infrastructure deployed."

# ── Step 4: Build and push Docker image to ACR ───────────────────────
IMAGE_TAG="${ACR_LOGIN_SERVER}/${PREFIX}-pipeline:latest"
info "Building and pushing container image to ACR..."
az acr build \
  --registry "$ACR_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --image "${PREFIX}-pipeline:latest" \
  --file "${REPO_ROOT}/Dockerfile" \
  "${REPO_ROOT}" \
  --no-logs || fail "ACR build failed. Ensure the Dockerfile is valid."
ok "Container image pushed: ${IMAGE_TAG}"

# ── Step 5: Update Container Apps Job with the real image ────────────
info "Updating Container Apps Job with new image..."
az containerapp job update \
  --name "$JOB_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --image "$IMAGE_TAG" \
  --output none || fail "Failed to update Container Apps Job."
ok "Pipeline job updated."

# ── Step 6: Create Web Knowledge Source ──────────────────────────────
info "Creating web knowledge source..."
SEARCH_BASE="https://${SEARCH_NAME}.search.windows.net"
API_VERSION="2025-05-01-preview"

# Idempotent: PUT is safe to call multiple times
HTTP_STATUS=$(curl -s -o /dev/null -w "%{http_code}" -X PUT \
  "${SEARCH_BASE}/knowledgesources/${PREFIX}-web-source?api-version=${API_VERSION}" \
  -H "Content-Type: application/json" \
  -H "api-key: ${SEARCH_API_KEY}" \
  -d "{
    \"name\": \"${PREFIX}-web-source\",
    \"type\": \"web\",
    \"webConfiguration\": {
      \"urls\": [\"https://reference.opcfoundation.org/\"],
      \"crawlDepth\": 3
    }
  }")

[[ "$HTTP_STATUS" =~ ^2 ]] || warn "Web knowledge source creation returned HTTP ${HTTP_STATUS} (may already exist)."
ok "Web knowledge source configured."

# ── Step 7: Create Knowledge Base ────────────────────────────────────
info "Creating knowledge base '${KB_NAME}'..."
HTTP_STATUS=$(curl -s -o /dev/null -w "%{http_code}" -X PUT \
  "${SEARCH_BASE}/knowledgebases/${KB_NAME}?api-version=${API_VERSION}" \
  -H "Content-Type: application/json" \
  -H "api-key: ${SEARCH_API_KEY}" \
  -d "{
    \"name\": \"${KB_NAME}\",
    \"description\": \"OPC UA specification knowledge base\",
    \"knowledgeSources\": [
      { \"knowledgeSourceName\": \"${PREFIX}-web-source\" }
    ],
    \"embeddingModel\": {
      \"azureOpenAIConnection\": {
        \"resourceUri\": \"${AOAI_ENDPOINT}\",
        \"deploymentId\": \"text-embedding-3-large\",
        \"modelName\": \"text-embedding-3-large\"
      }
    },
    \"chatCompletionModel\": {
      \"azureOpenAIConnection\": {
        \"resourceUri\": \"${AOAI_ENDPOINT}\",
        \"deploymentId\": \"gpt-4o\",
        \"modelName\": \"gpt-4o\"
      }
    }
  }")

[[ "$HTTP_STATUS" =~ ^2 ]] || warn "Knowledge base creation returned HTTP ${HTTP_STATUS} (may already exist)."
ok "Knowledge base '${KB_NAME}' configured."

# ── Step 8: Test query ───────────────────────────────────────────────
info "Running test query against the knowledge base..."
QUERY_RESULT=$(curl -s -X POST \
  "${SEARCH_BASE}/knowledgebases/${KB_NAME}/query?api-version=${API_VERSION}" \
  -H "Content-Type: application/json" \
  -H "api-key: ${SEARCH_API_KEY}" \
  -d '{ "query": "What is OPC UA?" }' 2>/dev/null) || true

if echo "$QUERY_RESULT" | jq -e '.answer' >/dev/null 2>&1; then
  ok "Test query succeeded."
else
  warn "Test query did not return an answer (knowledge base may still be indexing)."
fi

# ── Summary ───────────────────────────────────────────────────────────
echo ""
echo -e "${GREEN}════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN} Deployment Complete!${NC}"
echo -e "${GREEN}════════════════════════════════════════════════════════════${NC}"
echo ""
echo -e "  Resource Group:   ${BLUE}${RESOURCE_GROUP}${NC}"
echo -e "  Search Endpoint:  ${BLUE}${SEARCH_ENDPOINT}${NC}"
echo -e "  OpenAI Endpoint:  ${BLUE}${AOAI_ENDPOINT}${NC}"
echo -e "  ACR Server:       ${BLUE}${ACR_LOGIN_SERVER}${NC}"
echo -e "  MCP Endpoint:     ${BLUE}${MCP_ENDPOINT}${NC}"
echo ""
echo -e "  ${YELLOW}To configure GitHub Copilot CLI with this knowledge base:${NC}"
echo ""
echo -e "  Add to your MCP config (~/.copilot/mcp-config.json):"
echo ""
echo -e "    {"
echo -e "      \"servers\": {"
echo -e "        \"opcua-kb\": {"
echo -e "          \"type\": \"http\","
echo -e "          \"url\": \"${MCP_ENDPOINT}\","
echo -e "          \"headers\": {"
echo -e "            \"api-key\": \"<your-search-api-key>\""
echo -e "          }"
echo -e "        }"
echo -e "      }"
echo -e "    }"
echo ""
echo -e "${GREEN}════════════════════════════════════════════════════════════${NC}"
