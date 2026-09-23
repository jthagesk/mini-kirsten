// Mini-Nils i Azure: ett mottak (Container App) og én worker (Container Apps Job),
// with a Storage Queue, GitHub Actions-injected secrets, and Log Analytics for logs.
//
// Deployed by ../.github/deploy-azure.sh. The very first run (before the receiver app
// and worker job exist) uses a placeholder image, then the images are built in the
// course ACR and the template is deployed again. Later runs build first and deploy once.

targetScope = 'resourceGroup'

@description('Prefiks for alle ressursnavn, f.eks. lag03. Små bokstaver og tall.')
@minLength(3)
@maxLength(12)
param name string

@description('Name of the shared course Azure Container Registry.')
param acrName string

@description('Resource group containing the shared course Azure Container Registry.')
param acrResourceGroup string

@description('Resource ID of the shared user-assigned identity that has AcrPull on the course registry.')
param acrPullIdentityResourceId string

param location string = resourceGroup().location

@description('Bilde for mottaket. Plassholder til ACR er bygget.')
param receiverImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Bilde for workeren. Plassholder til ACR er bygget.')
param workerImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@secure()
@description('Classic GitHub token (public_repo) for this participant. It is supplied by GitHub Actions.')
param githubToken string

@secure()
@description('Anthropic API key supplied by the participant repository secret.')
param anthropicApiKey string

@secure()
@description('Webhook signing secret supplied by the participant repository secret.')
param webhookSecret string

@description('Anthropic workspace-ID for API keys that are not scoped to one workspace. Leave empty for workspace-scoped keys.')
param anthropicWorkspaceId string = ''

@description('Etiketten på en issue som starter agenten.')
param agentLabel string

@description('Navnet på agenten, Mini-<deltaker>. Står på commits, PR-er og resultattavla.')
param agentName string

@description('GitHub-loginet som eier GH_TOKEN. Andre kan nevne agenten med @login på pull requests. Tom verdi deaktiverer mention-modus.')
param agentMention string = ''

@description('Standardmodell for stagene (kan overstyres i agent/stages.json).')
param claudeModel string = 'claude-sonnet-5'

@description('The only repository the agent may work in. Receiver and worker reject all others.')
param targetRepo string = 'novanet/workshop.oslo-live'

@description('Skriv Claude Code sine verbose-diagnoser i worker-loggen.')
param claudeVerbose string = 'false'

var queueName = 'agent-tasks'
var runsTableName = 'agentRuns'
var uniq = uniqueString(resourceGroup().id, name)
// Storage Account names allow only lowercase letters and numbers, unlike the
// other Azure resource names that can retain the participant's hyphens.
var storageName = toLower('st${replace(name, '-', '')}${take(uniq, 8)}')

// ── Identitet ────────────────────────────────────────────────────────────────
// The course-owned identity pulls images from the shared ACR.

// ── Logger ───────────────────────────────────────────────────────────────────

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

// ── Queue ────────────────────────────────────────────────────────────────────

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }

  resource queues 'queueServices' = {
    name: 'default'
    resource tasks 'queues' = {
      name: queueName
    }
  }

  // Lab 3 stores sanitized run status in the same low-cost storage account.
  resource tables 'tableServices' = {
    name: 'default'
    resource runs 'tables' = {
      name: runsTableName
    }
  }
}

var storageConnection = 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'

// ── Shared Container Registry ────────────────────────────────────────────────
// The registry and its pull identity are created once by the course leader.
// Each participant owns only their namespaced images and their own app resources.

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' existing = {
  name: acrName
  scope: resourceGroup(acrResourceGroup)
}

// ── Container Apps environment ───────────────────────────────────────────────

resource environment_ 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${name}'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

// ── Mottak ───────────────────────────────────────────────────────────────────
// Keeps one replica running during the course, so GitHub's 10 second webhook
// timeout is not hit by a cold start. The worker still scales to zero.

resource receiver 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${name}-receiver'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${acrPullIdentityResourceId}': {}
    }
  }
  properties: {
    managedEnvironmentId: environment_.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
      }
      registries: [
        {
          server: acr.properties.loginServer
          identity: acrPullIdentityResourceId
        }
      ]
      secrets: [
        {
          name: 'queue-connection'
          value: storageConnection
        }
        {
          name: 'webhook-secret'
          value: webhookSecret
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'receiver'
          image: receiverImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'QUEUE_CONNECTION', secretRef: 'queue-connection' }
            { name: 'WEBHOOK_SECRET', secretRef: 'webhook-secret' }
            { name: 'QUEUE_NAME', value: queueName }
            { name: 'AGENT_LABEL', value: agentLabel }
            { name: 'AGENT_MENTION', value: agentMention }
            { name: 'TARGET_REPO', value: targetRepo }
            { name: 'RUNS_TABLE', value: runsTableName }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

// ── Worker ───────────────────────────────────────────────────────────────────
// Event-driven job: KEDA counts queue messages and starts one execution per message.
// Each execution fetches one message, solves the task, and exits. No loop.

resource worker 'Microsoft.App/jobs@2024-03-01' = {
  name: 'caj-${name}-worker'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${acrPullIdentityResourceId}': {}
    }
  }
  properties: {
    environmentId: environment_.id
    configuration: {
      triggerType: 'Event'
      replicaTimeout: 3600
      replicaRetryLimit: 0
      eventTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
        scale: {
          minExecutions: 0
          maxExecutions: 3
          pollingInterval: 30
          rules: [
            {
              name: 'queue'
              type: 'azure-queue'
              metadata: {
                queueName: queueName
                queueLength: '1'
                accountName: storage.name
              }
              auth: [
                {
                  secretRef: 'queue-connection'
                  triggerParameter: 'connection'
                }
              ]
            }
          ]
        }
      }
      registries: [
        {
          server: acr.properties.loginServer
          identity: acrPullIdentityResourceId
        }
      ]
      secrets: [
        {
          name: 'queue-connection'
          value: storageConnection
        }
        {
          name: 'anthropic-api-key'
          value: anthropicApiKey
        }
        {
          name: 'github-token'
          value: githubToken
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: workerImage
          resources: { cpu: json('1.0'), memory: '2Gi' }
          env: concat([
            { name: 'QUEUE_CONNECTION', secretRef: 'queue-connection' }
            // Memory is stored as a blob in the same storage account as the queue.
            // The container disappears after each job, so a file on disk would not survive.
            { name: 'MEMORY_CONNECTION', secretRef: 'queue-connection' }
            { name: 'ANTHROPIC_API_KEY', secretRef: 'anthropic-api-key' }
          ], empty(anthropicWorkspaceId) ? [] : [
            { name: 'ANTHROPIC_WORKSPACE_ID', value: anthropicWorkspaceId }
          ], [
            { name: 'GH_TOKEN', secretRef: 'github-token' }
            { name: 'QUEUE_NAME', value: queueName }
            { name: 'CLAUDE_MODEL', value: claudeModel }
            { name: 'CLAUDE_VERBOSE', value: claudeVerbose }
            { name: 'AGENT_NAME', value: agentName }
            { name: 'TARGET_REPO', value: targetRepo }
            { name: 'RUNS_TABLE', value: runsTableName }
          ])
        }
      ]
    }
  }
}

// ── Utdata ───────────────────────────────────────────────────────────────────

output receiverUrl string = 'https://${receiver.properties.configuration.ingress.fqdn}'
output acrName string = acr.name
output acrLoginServer string = acr.properties.loginServer
output receiverName string = receiver.name
output workerJobName string = worker.name
output runsTableName string = runsTableName
output logAnalyticsWorkspaceId string = logs.properties.customerId
