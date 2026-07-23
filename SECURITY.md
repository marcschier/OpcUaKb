# Security

This document describes how the **OPC UA Knowledge Base MCP Server**
(`OpcUaKb.McpServer`) aligns with the internal *Building Secure MCP Servers*
baseline, how to enable the hardened deployment, and which baseline requirements
are **Microsoft-internal-only** and therefore documented rather than implemented
in this public repository.

> **TL;DR** — The server ships **secure-by-default-capable but non-breaking**: an
> opt-in `MCP_AUTH_MODE=entra` turns on Entra ID bearer authentication (RFC 9728
> Protected Resource Metadata + audience-validated tokens, no passthrough), and an
> Azure Front Door + WAF edge (`infra/edge.bicep`) provides the DDoS-mitigation /
> traffic-normalization front door. The default `apikey` mode preserves the
> existing anonymous + api-key behaviour so current consumers keep working.

## Compliance matrix

| Baseline requirement | Status | How |
|---|---|---|
| C# paved-path package `Microsoft.ModelContextProtocol.HttpServer` | ⛔ **Internal-only** | Package is not on public NuGet. This repo uses the public `ModelContextProtocol.AspNetCore` SDK + `Microsoft.AspNetCore.Authentication.JwtBearer` for the equivalent token-validation pipeline, auth handler, and PRM endpoint. |
| Behind an approved Microsoft edge / DDoS proxy (Graph AI Gateway) | ⛔ **Internal-only** → ✅ analog | Graph AI Gateway onboarding is a corp-tenant process. This repo deploys **Azure Front Door + WAF** (`infra/edge.bicep`) as the personal-subscription analog: edge DDoS mitigation, traffic normalization, managed WAF rules (Premium). |
| MISE for Entra token acquisition/validation | ⛔ **Internal-only** → ✅ analog | MISE 1P is an internal feed. This repo validates tokens with `JwtBearer` against the tenant's published OIDC metadata, enforcing issuer + audience. |
| Dedicated Entra app (AppId) per server, scoped, audience-validated | ✅ **Wired, app documented** | `MCP_AUTH_MODE=entra` validates the token audience is **this server's** resource and (optionally) a required scope. No token passthrough. App registration steps below. |
| Protected Resource Metadata (RFC 9728) + `WWW-Authenticate` | ✅ **Implemented** | The SDK's `AddMcp` auth scheme serves `/.well-known/oauth-protected-resource` and the `WWW-Authenticate: Bearer resource_metadata=...` challenge. |
| HTTPS only; Bearer in `Authorization` header; never in URI/body | ✅ | ACA ingress is HTTPS (`allowInsecure: false`); Front Door route is `HttpsOnly` + `httpsRedirect`. Tokens are read from the `Authorization` header by `JwtBearer`. |
| No token passthrough (reject foreign-audience tokens) | ✅ | `ValidateAudience = true` against this server's `api://{ClientId}` / `MCP_RESOURCE_HOST`. |
| Rate limiting | ✅ | Partitioned per-IP anonymous vs authenticated limiter (existing). |
| Secure by default (explicit opt-out, not opt-in) | ✅ (deployment choice) | Secure mode is a single switch; fails closed if misconfigured (see below). Default remains `apikey` for backward-compat. |
| Secrets in Key Vault, never in code/logs/LLM responses | ✅ | `infra/edge.bicep` provisions Key Vault + grants the MCP server MI *Key Vault Secrets User*; the api-key is stored as a KV secret. Secrets are never logged (auth events log reason codes only). |
| Correct error codes (401 / 403 / 400) | ✅ | 401 (missing/invalid token, with challenge), 403 (valid token, insufficient scope / wrong edge), 400 (OAuth stubs in apikey mode). |
| Auth event logging without exposing secrets | ✅ | `[AUTH] Event=... Reason=...` structured logs; no tokens/keys. |
| STDIO transport guidance | ✅ n/a | The stdio transport (`--stdio`) is a local binary; it does not accept network tokens. |

## Auth modes

Controlled by `MCP_AUTH_MODE`:

- **`apikey`** (default) — Anonymous reads + the existing `mcp-access-key` api-key
  for `create_companion_projection`, `POST /upload-nodeset`, and
  `GET /mapping-artifacts`. Unchanged behaviour; no Entra dependency.
- **`entra`** — Every request except discovery (`/.well-known/*`) must present a
  valid Entra ID bearer token **issued for this server** (audience-validated).
  If `MCP_REQUIRED_SCOPE` is set, the token's `scp` claim must contain it.
  Requests missing/invalid tokens get `401` + `WWW-Authenticate`; valid tokens
  lacking the scope get `403`.

> **Fail-closed:** when `MCP_AUTH_MODE=entra` but `AZURE_AD_TENANT_ID`,
> `AZURE_AD_CLIENT_ID`, or `MCP_RESOURCE_HOST` are missing, the server throws at
> startup rather than silently downgrading to anonymous.

### Environment variables

| Variable | Mode | Meaning |
|---|---|---|
| `MCP_AUTH_MODE` | both | `apikey` (default) or `entra`. |
| `AZURE_AD_TENANT_ID` | entra | Entra tenant ID (authority = `https://login.microsoftonline.com/{tenant}/v2.0`). |
| `AZURE_AD_CLIENT_ID` | entra | Client ID of the dedicated app registration. |
| `MCP_RESOURCE_AUDIENCE` | entra | Optional explicit audience; defaults to `api://{AZURE_AD_CLIENT_ID}`. |
| `MCP_REQUIRED_SCOPE` | entra | Optional required `scp` (e.g. `Mcp.Tools`). Empty = any valid token for this resource. |
| `MCP_RESOURCE_HOST` | entra | Canonical public URL for the PRM `resource` field (usually the Front Door endpoint). |
| `MCP_FRONTDOOR_ID` | both | Optional `X-Azure-FDID` value; when set, requests not carrying it get `403` (locks ingress to the approved edge). |

## Enabling the hardened deployment

### 1. Deploy the edge (Front Door + WAF + Key Vault)

```bash
az deployment group create -g rg-opcua-kb -f infra/edge.bicep \
  -p mcpAccessKey='<same value as the container mcp-access-key>' \
     frontDoorSku='Premium_AzureFrontDoor'   # Premium enables managed WAF rulesets
```

Outputs: `frontDoorEndpoint` (the new public URL), `frontDoorId` (X-Azure-FDID),
`keyVaultUri`.

### 2. Register the dedicated Entra app (one per server)

```bash
# Create the app registration
appId=$(az ad app create --display-name "OpcUaKb MCP Server" \
  --query appId -o tsv)

# Expose an API + a scope the clients request
az ad app update --id "$appId" \
  --identifier-uris "api://$appId"
# In the portal (or via Graph): Expose an API → Add a scope → Mcp.Tools
#   Admin consent required; who can consent: Admins + users.

# (Optional) create a service principal so tokens can be issued
az ad sp create --id "$appId"
```

You now have `AZURE_AD_CLIENT_ID=$appId` and `AZURE_AD_TENANT_ID=$(az account show --query tenantId -o tsv)`.

### 3. Turn on secure mode + repoint consumers (non-breaking order)

1. **Deploy** the edge (step 1) and **repoint consumers first** while the server
   is still anonymous:
   - Hosted Agent: `azd env set MCP_SERVER_URL https://<frontDoorEndpoint>/`
   - Copilot CLI: set the `opcua-kb-tools` `url` in `mcp-config.json` to the
     Front Door endpoint.
2. **Enable secure mode** by redeploying `main.bicep` with:
   ```bash
   az deployment group create -g rg-opcua-kb -f infra/main.bicep \
     -p mcpAccessKey='<key>' mcpAuthMode='entra' \
        entraTenantId='<tenant>' entraClientId='<appId>' \
        mcpRequiredScope='Mcp.Tools' \
        mcpResourceHost='https://<frontDoorEndpoint>/' \
        mcpFrontDoorId='<frontDoorId>'
   ```
   Clients must now acquire a token for `api://<appId>` (scope
   `api://<appId>/Mcp.Tools`) via MSAL / OneAuth and send it as
   `Authorization: Bearer <token>`.
3. **Lock ingress**: with `mcpFrontDoorId` set, the server rejects any request
   that didn't arrive through Front Door (`403`). Direct ACA-FQDN calls stop
   working — verify all consumers use the Front Door URL first.

## Internal-only follow-ups (production at Microsoft)

For a true production deployment inside Microsoft, additionally:

- Migrate to the paved-path **`Microsoft.ModelContextProtocol.HttpServer`**
  package and **MISE** — see the internal
  [MCP + MISE adoption guide](https://eng.ms/docs/microsoft-security/identity/app-plat-and-graph/app-vertical/aad-first-party-apps/identity-platform-and-access-management/microsoft-identity-platform/secure-mcp-servers).
- Front the server with **Microsoft Graph AI Gateway** / an approved DDoS
  shielding edge — see
  [Approved proxies and DDoS shielding](https://eng.ms/docs/initiatives/project-standard/standards-categories/sc-networking/ddos/ads/index)
  and [onboard to AI Gateway](https://eng.ms/docs/products/microsoft-graph-service/microsoft-graph/onboard-to-ai-gateway).

## Reporting

Do not file security issues as public GitHub issues. Report through the
appropriate internal security channel.
