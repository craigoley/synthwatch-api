// SynthWatch API — Azure Function App on the Flex Consumption plan.
//
// Flex Consumption is required: .NET 10 isolated CANNOT run on Linux Consumption.
// Flex scales to zero (≈free at this traffic) and supports the dotnet-isolated 10.0 runtime.
//
// The Function App gets a SYSTEM-ASSIGNED managed identity. That identity is what:
//   1. authenticates to Azure Postgres (no password — see README manual steps), and
//   2. authenticates to the deployment / AzureWebJobs storage (role assignments below).
//
// Deploy into the EXISTING synthwatch-rg (scope = resourceGroup). Does NOT create the
// Postgres server — it already exists.
//
//   az deployment group create -g synthwatch-rg -f infra/main.bicep \
//     -p pgHost=<server>.postgres.database.azure.com \
//        allowedCorsOrigins='["https://<dashboard>.vercel.app","https://<preview>.vercel.app"]'

targetScope = 'resourceGroup'

@description('Function App (and default Postgres MI role) name.')
param functionAppName string = 'synthwatch-api'

@description('Azure region. The existing resource group is in eastus2.')
param location string = 'eastus2'

@description('Azure Postgres Flexible Server FQDN, e.g. myserver.postgres.database.azure.com')
param pgHost string

@description('Database name.')
param pgDatabase string = 'synthwatch'

@description('Postgres role for the Function App MI. For a system-assigned identity this is the Function App name — it must match the principal created via pgaadauth_create_principal.')
param pgMiUsername string = functionAppName

@description('Allowed CORS origins (scheme + host, no trailing slash). Applied as PLATFORM CORS (siteConfig.cors) — the only layer that can answer the OPTIONS preflight the host intercepts. Defaults to the prod dashboard so it survives redeploys; never "*".')
param allowedCorsOrigins array = [
  'https://synthwatch-dashboard.vercel.app'
]

@description('Max Flex Consumption instances.')
param maximumInstanceCount int = 40

@description('Per-instance memory (MB) for Flex Consumption.')
@allowed([512, 2048, 4096])
param instanceMemoryMB int = 2048

@description('Phase 12 auth — comma-separated admin emails. ★ The API enforces ADMIN from THIS setting (not the dashboard Vercel env), so admins are recognized + cannot be locked out. Empty = no admins until set.')
param adminEmails string = ''

@description('Phase 12 auth — the ACS sender for OTP / access-request emails. The API reads THIS (AuthFunctions/AcsEmailSender → AUTH_EMAIL_FROM) as the message senderAddress (the From: header). NON-secret (a property of the ACS-owned managed domain); committed so a redeploy can\'t blank it. Value = the Verified fromSenderDomain on synthwatch-email (donotreply@<guid>.azurecomm.net) — identical to the runner\'s ALERT_EMAIL_FROM, which is proven to deliver. ★ NOTE (corrects #83): delivered mail shows a "<guid>.us3.azurecomm.net" Return-Path / mailed-by — that is ACS\'s REGIONAL envelope domain for the unitedstates data location, NOT this senderAddress and NOT a value to set here (it appears regardless of AUTH_EMAIL_FROM, for the runner too). The actual problem is SPAM placement (shared azurecomm.net reputation) — fix with a custom verified domain + SPF/DKIM/DMARC (follow-up), not by changing this address.')
param authEmailFrom string = 'donotreply@0ad660ff-ac71-4b63-a5f6-ce885666c796.azurecomm.net'

@description('Phase 12 auth — ACS resource endpoint for MI-based email send. NON-secret (the resource\'s public endpoint). The API MI sends via DefaultAzureCredential against this; it holds the "Communication and Email Service Owner" role on synthwatch-acs (already assigned). Empty = MI send off (set ACS_EMAIL_CONNECTION_STRING as a fallback instead).')
param acsEmailEndpoint string = 'https://synthwatch-acs.unitedstates.communication.azure.com/'

@description('Phase 12 auth — slice 2 GATE master switch. DEFAULT OFF: the AuthorizationMiddleware is inert and writes pass as today. ★ Flip to true ONLY together with slice 3 (the dashboard sending session tokens) — turning it on before the dashboard sends tokens would 401 every write.')
param authEnforcementEnabled bool = false

@description('Trace AI Insights (slice 2) — Azure OpenAI endpoint, e.g. https://synthwatch-aoai.openai.azure.com/. DEFAULT EMPTY = INERT: POST /api/runs/{id}/ai-insights returns "not configured" until set. ★ Deploy prereq: also grant the Function App MI "Cognitive Services OpenAI User" on synthwatch-aoai (see the PR).')
param azureOpenAiEndpoint string = ''

@description('Trace AI Insights — the AOAI chat deployment name. gpt-5-mini (shared with the runner RCA).')
param azureOpenAiDeployment string = 'gpt-5-mini'

@description('Trace AI Insights — the AOAI REST api-version.')
param azureOpenAiApiVersion string = '2025-04-01-preview'

@description('Existing runner-owned artifacts storage account (failure screenshots + Playwright traces). The Function App reads blobs from here via the trace/screenshot proxies.')
param artifactsStorageAccountName string = 'synthwatche24e33105c'

var storageAccountName = take(toLower('st${uniqueString(resourceGroup().id, functionAppName)}'), 24)
var deploymentContainerName = 'app-package'

// Built-in role definition IDs.
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var storageQueueDataContributorRoleId = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var storageBlobDataReaderRoleId = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: deploymentContainerName
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${functionAppName}-law'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${functionAppName}-ai'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${functionAppName}-plan'
  location: location
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  kind: 'functionapp'
  properties: {
    reserved: true
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    siteConfig: {
      appSettings: [
        // Identity-based AzureWebJobs storage (no account key).
        {
          name: 'AzureWebJobsStorage__accountName'
          value: storage.name
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        // Postgres coordinates — NO password. Auth is managed identity (see README).
        {
          name: 'Postgres__Host'
          value: pgHost
        }
        {
          name: 'Postgres__Database'
          value: pgDatabase
        }
        {
          name: 'Postgres__Username'
          value: pgMiUsername
        }
        // Phase 12 auth (slice 1). ADMIN_EMAILS is the SECURITY source of truth for admin (the API
        // enforces, not the dashboard). AUTH_EMAIL_FROM + ACS_EMAIL_ENDPOINT drive MI-based OTP email.
        {
          name: 'ADMIN_EMAILS'
          value: adminEmails
        }
        {
          name: 'AUTH_EMAIL_FROM'
          value: authEmailFrom
        }
        {
          name: 'ACS_EMAIL_ENDPOINT'
          value: acsEmailEndpoint
        }
        // Phase 12 auth slice 2 — the gate. DEFAULT OFF (inert); flipped on with slice 3.
        {
          name: 'AUTH_ENFORCEMENT_ENABLED'
          value: string(authEnforcementEnabled)
        }
        // Trace AI Insights (slice 2). DEFAULT endpoint EMPTY = INERT until the MI role + endpoint are set.
        {
          name: 'AZURE_OPENAI_ENDPOINT'
          value: azureOpenAiEndpoint
        }
        {
          name: 'AZURE_OPENAI_DEPLOYMENT'
          value: azureOpenAiDeployment
        }
        {
          name: 'AZURE_OPENAI_API_VERSION'
          value: azureOpenAiApiVersion
        }
      ]
      // PLATFORM CORS. The Functions host answers the OPTIONS preflight itself (before the worker
      // runs), so this — not app code — is what makes preflight work for PATCH/POST/DELETE.
      // Explicit origins only (never "*"); the host echoes the matched origin.
      cors: {
        allowedOrigins: allowedCorsOrigins
        supportCredentials: false
      }
    }
  }
}

// The Function App MI needs data-plane access to the storage account (deployment package +
// AzureWebJobs host state) since shared-key access is disabled.
resource blobOwnerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionApp.id, storageBlobDataOwnerRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource queueContributorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, functionApp.id, storageQueueDataContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageQueueDataContributorRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// The Function App's MI needs READ access to the runner-owned artifacts account so the
// trace/screenshot proxies can stream blobs. Durable Bicep declaration of a grant that was first
// applied live (the config-drift lesson: don't leave critical access as a manual-only setting).
resource artifactsStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: artifactsStorageAccountName
}

resource artifactsBlobReaderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(artifactsStorage.id, functionApp.id, storageBlobDataReaderRoleId)
  scope: artifactsStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReaderRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

@description('Base URL of the deployed Function App.')
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'

@description('API base URL (routes are prefixed with /api).')
output apiBaseUrl string = 'https://${functionApp.properties.defaultHostName}/api'

@description('System-assigned MI principal (object) ID — useful for diagnostics.')
output functionAppPrincipalId string = functionApp.identity.principalId

@description('Function App name — also the Postgres MI role name to create.')
output functionAppNameOut string = functionApp.name
