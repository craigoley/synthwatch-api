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

var storageAccountName = take(toLower('st${uniqueString(resourceGroup().id, functionAppName)}'), 24)
var deploymentContainerName = 'app-package'

// Built-in role definition IDs.
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var storageQueueDataContributorRoleId = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'

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

@description('Base URL of the deployed Function App.')
output functionAppUrl string = 'https://${functionApp.properties.defaultHostName}'

@description('API base URL (routes are prefixed with /api).')
output apiBaseUrl string = 'https://${functionApp.properties.defaultHostName}/api'

@description('System-assigned MI principal (object) ID — useful for diagnostics.')
output functionAppPrincipalId string = functionApp.identity.principalId

@description('Function App name — also the Postgres MI role name to create.')
output functionAppNameOut string = functionApp.name
