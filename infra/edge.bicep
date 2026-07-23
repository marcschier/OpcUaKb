// ═══════════════════════════════════════════════════════════════════════
// OPC UA KB — Secure edge (Azure Front Door + WAF) + Key Vault
//
// Deployed STANDALONE (separate from main.bicep) so it can be applied without
// re-running the whole stack. Fronts the existing MCP Container App with Azure
// Front Door (edge DDoS mitigation + traffic normalization; managed WAF rules
// on Premium) and moves the MCP application key into Key Vault.
//
// See SECURITY.md for how this maps to the "Building Secure MCP Servers"
// baseline and the follow-up cutover steps (repoint consumers, set
// MCP_FRONTDOOR_ID to lock direct ingress).
//
//   az deployment group create -g rg-opcua-kb -f infra/edge.bicep \
//     -p mcpAccessKey=<key>            # same value as the container's mcp-access-key
// ═══════════════════════════════════════════════════════════════════════

@description('Prefix used to derive resource names (must match main.bicep)')
param prefix string = 'opcua-kb'

@description('Location for Key Vault (Front Door is a global resource)')
param location string = resourceGroup().location

@description('Container App name of the MCP server that Front Door fronts')
param mcpAppName string = '${prefix}-mcp-server'

@description('Front Door SKU. Premium enables managed WAF rulesets (DRS + Bot); Standard supports custom rules + platform DDoS only.')
@allowed([
  'Standard_AzureFrontDoor'
  'Premium_AzureFrontDoor'
])
param frontDoorSku string = 'Standard_AzureFrontDoor'

@description('MCP application key to store in Key Vault (same value as the container app mcp-access-key secret)')
@secure()
@minLength(32)
param mcpAccessKey string

var afdProfileName = '${prefix}-afd'
var afdEndpointName = prefix
var afdOriginGroupName = 'mcp-origin-group'
var afdOriginName = 'mcp-origin'
var afdRouteName = 'mcp-route'
var wafPolicyName = replace('${prefix}waf', '-', '')
var securityPolicyName = 'mcp-security-policy'
var kvName = take('${replace(prefix, '-', '')}kv${uniqueString(resourceGroup().id)}', 24)

// Existing MCP Container App — used for the origin host name and its managed identity.
resource mcpApp 'Microsoft.App/containerApps@2024-03-01' existing = {
  name: mcpAppName
}

// ── Key Vault (secrets management per the baseline) ──────────────────
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: kvName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
  }
}

resource mcpAccessKeySecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'mcp-access-key'
  properties: {
    value: mcpAccessKey
  }
}

// Let the MCP server's managed identity read the secret (for ACA Key Vault secret references).
resource kvSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, mcpApp.id, 'kv-secrets-user')
  scope: keyVault
  properties: {
    principalId: mcpApp.identity.principalId
    // Key Vault Secrets User
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalType: 'ServicePrincipal'
  }
}

// ── Front Door WAF policy ────────────────────────────────────────────
// Managed rule sets (DRS + Bot Manager) require the Premium SKU. On Standard
// the policy still runs in Prevention mode and benefits from Front Door's
// platform-level DDoS protection and traffic normalization.
resource wafPolicy 'Microsoft.Network/FrontDoorWebApplicationFirewallPolicies@2022-05-01' = {
  name: wafPolicyName
  location: 'Global'
  sku: {
    name: frontDoorSku
  }
  properties: {
    policySettings: {
      enabledState: 'Enabled'
      mode: 'Prevention'
    }
    managedRules: frontDoorSku == 'Premium_AzureFrontDoor'
      ? {
          managedRuleSets: [
            {
              ruleSetType: 'Microsoft_DefaultRuleSet'
              ruleSetVersion: '2.1'
              ruleSetAction: 'Block'
            }
            {
              ruleSetType: 'Microsoft_BotManagerRuleSet'
              ruleSetVersion: '1.0'
            }
          ]
        }
      : {
          managedRuleSets: []
        }
  }
}

// ── Azure Front Door (Standard/Premium) ─────────────────────────────
resource afdProfile 'Microsoft.Cdn/profiles@2023-05-01' = {
  name: afdProfileName
  location: 'Global'
  sku: {
    name: frontDoorSku
  }
}

resource afdEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2023-05-01' = {
  parent: afdProfile
  name: afdEndpointName
  location: 'Global'
  properties: {
    enabledState: 'Enabled'
  }
}

resource afdOriginGroup 'Microsoft.Cdn/profiles/originGroups@2023-05-01' = {
  parent: afdProfile
  name: afdOriginGroupName
  properties: {
    loadBalancingSettings: {
      sampleSize: 4
      successfulSamplesRequired: 3
    }
    healthProbeSettings: {
      probePath: '/'
      probeRequestType: 'HEAD'
      probeProtocol: 'Https'
      probeIntervalInSeconds: 100
    }
  }
}

resource afdOrigin 'Microsoft.Cdn/profiles/originGroups/origins@2023-05-01' = {
  parent: afdOriginGroup
  name: afdOriginName
  properties: {
    hostName: mcpApp.properties.configuration.ingress.fqdn
    originHostHeader: mcpApp.properties.configuration.ingress.fqdn
    httpPort: 80
    httpsPort: 443
    priority: 1
    weight: 1000
    enabledState: 'Enabled'
    enforceCertificateNameCheck: true
  }
}

resource afdRoute 'Microsoft.Cdn/profiles/afdEndpoints/routes@2023-05-01' = {
  parent: afdEndpoint
  name: afdRouteName
  dependsOn: [
    afdOrigin
  ]
  properties: {
    originGroup: {
      id: afdOriginGroup.id
    }
    supportedProtocols: [
      'Https'
    ]
    patternsToMatch: [
      '/*'
    ]
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Enabled'
    linkToDefaultDomain: 'Enabled'
  }
}

// Associate the WAF policy with the endpoint.
resource securityPolicy 'Microsoft.Cdn/profiles/securityPolicies@2023-05-01' = {
  parent: afdProfile
  name: securityPolicyName
  properties: {
    parameters: {
      type: 'WebApplicationFirewall'
      wafPolicy: {
        id: wafPolicy.id
      }
      associations: [
        {
          domains: [
            {
              id: afdEndpoint.id
            }
          ]
          patternsToMatch: [
            '/*'
          ]
        }
      ]
    }
  }
}

// ── Outputs ──────────────────────────────────────────────────────────
@description('Public Front Door endpoint host name — the new MCP server URL. Point consumers here.')
output frontDoorHostName string = afdEndpoint.properties.hostName

@description('Front Door endpoint URL')
output frontDoorEndpoint string = 'https://${afdEndpoint.properties.hostName}/'

@description('Front Door ID (X-Azure-FDID). Set as MCP_FRONTDOOR_ID on the container to reject non-Front-Door traffic.')
output frontDoorId string = afdProfile.properties.frontDoorId

@description('Key Vault URI (for ACA Key Vault secret references)')
output keyVaultUri string = keyVault.properties.vaultUri
