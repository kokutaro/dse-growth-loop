targetScope = 'resourceGroup'

@allowed([1379060968])
param repositoryId int
@minValue(1)
@maxValue(999999999)
param prNumber int
@minLength(40)
@maxLength(40)
param headSha string
param apiImage string
param environmentId string
param pullIdentityId string
param expiresAt string
@minLength(1)
@maxLength(28)
param revisionSuffix string

var name = 'dse-pr-${repositoryId}-${prNumber}'

// This is the only PR-owned resource. No database, queue, Key Vault, role assignment,
// production identity, shared environment, or resource-group deletion is deployed here.
resource preview 'Microsoft.App/containerApps@2025-07-01' = {
  name: name
  location: 'japaneast'
  tags: {
    'managed-by': 'dse-preview-v1'
    'repository-id': string(repositoryId)
    'pull-request': string(prNumber)
    'head-sha': headSha
    'expires-at': expiresAt
  }
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${pullIdentityId}': {} } }
  properties: {
    managedEnvironmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'auto', allowInsecure: false }
      registries: [{ server: 'dseacrypbunss3xh66m.azurecr.io', identity: pullIdentityId }]
      identitySettings: [{ identity: pullIdentityId, lifecycle: 'None' }]
    }
    template: {
      revisionSuffix: revisionSuffix
      containers: [{
        name: 'api'
        image: apiImage
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: [
          { name: 'ASPNETCORE_ENVIRONMENT', value: 'Development' }
          { name: 'DOTNET_ENVIRONMENT', value: 'Development' }
          { name: 'Data__Mode', value: 'Demo' }
          { name: 'Data__LocalPath', value: '/tmp/${name}' }
          { name: 'Ingestion__RunWorker', value: 'true' }
          { name: 'Ai__Mode', value: 'Mock' }
          { name: 'AiUsage__Stopped', value: 'true' }
          { name: 'AiUsage__LiveOptIn', value: 'false' }
          { name: 'AiUsage__MaxOutputTokens', value: '3000' }
          { name: 'Operations__AnalysisMode', value: 'Manual' }
          { name: 'Operations__RecoveryMode', value: 'Manual' }
          { name: 'Operations__WarmupEnabled', value: 'false' }
          { name: 'Logging__LogLevel__Default', value: 'Warning' }
        ]
        probes: [
          { type: 'Startup', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 5, failureThreshold: 60 }
          { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 30 }
        ]
      }]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        cooldownPeriod: 300
        rules: [{ name: 'http', http: { metadata: { concurrentRequests: '1' } } }]
      }
    }
  }
}

output url string = 'https://${preview.properties.configuration.ingress.fqdn}'
