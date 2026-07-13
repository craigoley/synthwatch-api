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

// ★★ TRAP — READ BEFORE YOU DEPLOY THIS BICEP. This param is NEVER applied by the CD (`.github/workflows/
// deploy.yml` is a code-only functions-action push; it does NOT run `az deployment group create`). ADMIN_EMAILS
// — the API's SOLE source of admin identity — is set OUT-OF-BAND (`az functionapp config appsettings set …
// --settings ADMIN_EMAILS=…`). A manual `az deployment group create` of this template WITHOUT `-p adminEmails=…`
// applies this default ('') and WIPES ADMIN_EMAILS, LOCKING OUT EVERY ADMIN. Always pass the live value
// (`az functionapp config appsettings list … --query "[?name=='ADMIN_EMAILS'].value" -o tsv`) when applying
// this bicep. A deploy guard (scripts/assert-admin-emails.sh, wired into deploy.yml) fails loud on an empty list.
@description('Phase 12 auth — comma-separated admin emails. ★ The API enforces ADMIN from THIS setting (not the dashboard Vercel env), so admins are recognized + cannot be locked out. Empty = no admins until set. ★ NOT applied by the CD (set out-of-band) — a bicep apply without -p adminEmails=… WIPES it; see the trap comment above.')
param adminEmails string = ''

@description('Phase 12 auth — the ACS sender for OTP / access-request emails. The API reads THIS (AuthFunctions/AcsEmailSender → AUTH_EMAIL_FROM) as the message senderAddress (the From: header). NON-secret (a property of the ACS-owned managed domain); committed so a redeploy can\'t blank it. Value = the Verified fromSenderDomain on synthwatch-email (donotreply@<guid>.azurecomm.net) — identical to the runner\'s ALERT_EMAIL_FROM, which is proven to deliver. ★ NOTE (corrects #83): delivered mail shows a "<guid>.us3.azurecomm.net" Return-Path / mailed-by — that is ACS\'s REGIONAL envelope domain for the unitedstates data location, NOT this senderAddress and NOT a value to set here (it appears regardless of AUTH_EMAIL_FROM, for the runner too). The actual problem is SPAM placement (shared azurecomm.net reputation) — fix with a custom verified domain + SPF/DKIM/DMARC (follow-up), not by changing this address.')
param authEmailFrom string = 'donotreply@0ad660ff-ac71-4b63-a5f6-ce885666c796.azurecomm.net'

@description('Phase 12 auth — ACS resource endpoint for MI-based email send. NON-secret (the resource\'s public endpoint). The API MI sends via DefaultAzureCredential against this; it holds the "Communication and Email Service Owner" role on synthwatch-acs (already assigned). Empty = MI send off (set ACS_EMAIL_CONNECTION_STRING as a fallback instead).')
param acsEmailEndpoint string = 'https://synthwatch-acs.unitedstates.communication.azure.com/'

@description('Phase 12 auth — slice 2 GATE master switch. FAIL-CLOSED DEFAULT true: the AuthorizationMiddleware ENFORCES (editor/admin session required for writes). Slice 3 shipped — the dashboard sends session tokens and prod has AUTH_ENFORCEMENT_ENABLED=true (live-verified 2026-07-05). ★ A default-param redeploy PRESERVES enforcement; disabling it is an EXPLICIT opt-out (pass authEnforcementEnabled=false) that opens every write, all paid-AOAI endpoints, and reconcile/apply at once. This mirrors #161 (the RUNTIME flag defaults fail-closed / ON-when-unset) one layer down at infra, and protects the #162/#154 read-gate + forensic-endpoint auth work: without this, a default-param `az deployment group create` (see README Deploy — it passes only pgHost/allowedCorsOrigin) would write string(false) and silently disable prod enforcement.')
param authEnforcementEnabled bool = true

@description('Trace AI Insights (slice 2) — Azure OpenAI endpoint, e.g. https://synthwatch-aoai.openai.azure.com/. DEFAULT EMPTY = INERT: POST /api/runs/{id}/ai-insights returns "not configured" until set. ★ Deploy prereq: also grant the Function App MI "Cognitive Services OpenAI User" on synthwatch-aoai (see the PR).')
param azureOpenAiEndpoint string = ''

@description('Trace AI Insights — the AOAI chat deployment name. gpt-5-mini (shared with the runner RCA).')
param azureOpenAiDeployment string = 'gpt-5-mini'

@description('Trace AI Insights — the AOAI REST api-version.')
param azureOpenAiApiVersion string = '2025-04-01-preview'

@description('Trace AI Insights — max_completion_tokens budget. gpt-5-mini is a REASONING model: hidden reasoning tokens consume this before the visible JSON, so a tight budget truncates (finish_reason=length). The extracted summary is also hard-bounded (TraceExtractor) so input can\'t blow it.')
param azureOpenAiMaxTokens int = 16000

@description('Trace AI Insights — reasoning_effort (minimal|low|medium|high). "low" keeps the reasoning-token spend down so the output fits the budget. Empty = omit the field.')
param azureOpenAiReasoningEffort string = 'low'

@description('GET /reports/cost — the runner (browser) job vCPU allocation being priced. The rate is DERIVED from TWO ACA Consumption meters × the live allocation (cpu×0.000024 + mem×0.000003) — NOT a single blended scalar. ★ The old 0.00003 "vCPU rate" was really the 1.0/2 blend, so it was 2× wrong at 2.0/4. Mirror the runner infra (synthwatch infra/main.bicep runner jobs); a resize re-prices with NO api code change. String so a decimal survives ARM.')
param runnerCpu string = '2.0'
@description('GET /reports/cost — the runner (browser) job memory (GiB) being priced. See runnerCpu. String so a decimal survives ARM.')
param runnerMemoryGib string = '4'
@description('GET /reports/cost — an OPTIONAL explicit $/active-second override (env COST_RATE_PER_ACTIVE_SECOND). Empty = DERIVE from runnerCpu/runnerMemoryGib (the normal case). Set only to re-price without touching the allocation. String so a decimal survives ARM.')
param costRateOverridePerActiveSecond string = ''
@description('GET /reports/cost — the date the rate basis was last set (YYYY-MM-DD), ECHOED in the response. Update alongside the meters/allocation.')
param costRateSetDate string = '2026-07-12'

@description('Existing runner-owned artifacts storage account (failure screenshots + Playwright traces). The Function App reads blobs from here via the trace/screenshot proxies.')
param artifactsStorageAccountName string = 'synthwatche24e33105c'

@description('Runner-owned ACA jobs the API MI starts on-demand: "Run now" (test-send/run) starts the runner job; POST /api/reconcile/trigger starts the reconcile job. The API MI needs Container Apps Jobs Operator on each (covers Microsoft.App/jobs/start/action).')
param runnerJobName string = 'synthwatch-runner-job'
param reconcileJobName string = 'synthwatch-reconcile-job'

@description('AOAI account the RCA path (AiInsights / LocationDiff) calls via DefaultAzureCredential — the MI needs Cognitive Services OpenAI User.')
param aoaiAccountName string = 'synthwatch-aoai'

@description('ACS resource the OTP-email sender (AcsEmailSender) uses via DefaultAzureCredential — the MI needs Communication and Email Service Owner.')
param acsResourceName string = 'synthwatch-acs'

@description('Model-B credential-value encryption key (app setting CRED_ENC_KEY) — base64 of 32 random bytes (AES-256). The api ENCRYPTS secret_headers/login_credentials values before store; the runner DECRYPTS at run time (CredCrypto.cs ↔ runner/crypto.ts). ★ MUST be the SAME key value as the runner job\'s CRED_ENC_KEY (from ~/.synthwatch.env) or the runner cannot decrypt what the api wrote. Secret — supplied at deploy, NEVER committed. Default \'\' → the api fail-CLOSES on encrypt/decrypt until set. Bicep-owned so a bicep redeploy re-asserts it (the code-only deploy.yml never touches app settings).')
@secure()
param credEncKey string = ''

var storageAccountName = take(toLower('st${uniqueString(resourceGroup().id, functionAppName)}'), 24)
var deploymentContainerName = 'app-package'

// Built-in role definition IDs.
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var storageQueueDataContributorRoleId = '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
var storageBlobDataReaderRoleId = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
// Container Apps Jobs Operator — includes Microsoft.App/jobs/start/action (the on-demand start the MI uses).
var containerAppsJobsOperatorRoleId = 'b9a307c4-5aa3-4b52-ba60-2b17c136cd7b'
// Cognitive Services OpenAI User — data-plane chat-completions on the AOAI account (the RCA path).
var cognitiveServicesOpenAiUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
// Communication and Email Service Owner — send email via ACS (the OTP sign-in / edit-access mails).
var communicationEmailServiceOwnerRoleId = '09976791-48a7-449e-bb21-39d1a415f350'

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
        // Phase 12 auth slice 2 — the gate. FAIL-CLOSED DEFAULT true (enforcing); default-param deploy keeps it on. See #161/#162.
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
        {
          name: 'AZURE_OPENAI_MAX_TOKENS'
          value: string(azureOpenAiMaxTokens)
        }
        {
          name: 'AZURE_OPENAI_REASONING_EFFORT'
          value: azureOpenAiReasoningEffort
        }
        // GET /reports/cost — the rate is DERIVED (two ACA meters × the live runner allocation), so we stamp
        // the allocation the runner jobs carry (SYNTHWATCH_RUNNER_CPU/MEMORY_GIB). CostRate.cs blends them:
        // cpu×0.000024 + mem×0.000003. A resize re-prices with NO api code deploy. The optional override is
        // COST_RATE_PER_ACTIVE_SECOND ('' = derive). SetDate is echoed in the response.
        {
          name: 'SYNTHWATCH_RUNNER_CPU'
          value: runnerCpu
        }
        {
          name: 'SYNTHWATCH_RUNNER_MEMORY_GIB'
          value: runnerMemoryGib
        }
        {
          name: 'COST_RATE_PER_ACTIVE_SECOND'
          value: costRateOverridePerActiveSecond
        }
        {
          name: 'COST_RATE_SET_DATE'
          value: costRateSetDate
        }
        {
          // Model-B credential-value encryption key (CredCrypto.cs). Bicep-owned (value from the @secure
          // param → re-asserted every bicep deploy; the code-only deploy.yml never touches app settings).
          // '' when unset → the api fail-CLOSES on encrypt/decrypt. MUST equal the runner's CRED_ENC_KEY.
          name: 'CRED_ENC_KEY'
          value: credEncKey
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

// The Function App's MI starts runner-owned ACA jobs on-demand: "Run now" (ChecksRun/ChannelTest) starts the
// runner job; POST /api/reconcile/trigger starts the reconcile job. Both need Container Apps Jobs Operator
// (Microsoft.App/jobs/start/action). PROVEN live (an off-cron reconcile execution fired + Succeeded as this MI).
// Durable Bicep declaration of grants that were first applied manually — the config-drift lesson: a critical
// "works-as-admin / 403s-as-the-MI" grant must not live only as a CLI assignment that a redeploy could lose.
resource runnerJob 'Microsoft.App/jobs@2024-03-01' existing = {
  name: runnerJobName
}
resource reconcileJob 'Microsoft.App/jobs@2024-03-01' existing = {
  name: reconcileJobName
}

resource runnerJobOperatorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(runnerJob.id, functionApp.id, containerAppsJobsOperatorRoleId)
  scope: runnerJob
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', containerAppsJobsOperatorRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource reconcileJobOperatorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(reconcileJob.id, functionApp.id, containerAppsJobsOperatorRoleId)
  scope: reconcileJob
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', containerAppsJobsOperatorRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// The MI calls the AOAI account (RCA) and ACS (OTP email) via DefaultAzureCredential. Both grants existed only
// as MANUAL CLI assignments (config drift — exactly what the grant-coverage CI check guards). Durable Bicep
// declarations matching the live RBAC; deterministic guid() names make redeploys idempotent.
resource aoaiAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: aoaiAccountName
}
resource acsResource 'Microsoft.Communication/communicationServices@2023-04-01' existing = {
  name: acsResourceName
}

resource aoaiOpenAiUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aoaiAccount.id, functionApp.id, cognitiveServicesOpenAiUserRoleId)
  scope: aoaiAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAiUserRoleId)
    principalId: functionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource acsEmailOwnerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acsResource.id, functionApp.id, communicationEmailServiceOwnerRoleId)
  scope: acsResource
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', communicationEmailServiceOwnerRoleId)
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
