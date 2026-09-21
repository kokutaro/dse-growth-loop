param location string
param environmentId string
param registryName string
param apiIdentityId string
param apiClientId string
param workerIdentityId string
param workerClientId string
param cosmosEndpoint string
param storageName string
param keyVaultUri string
param foundryEndpoint string
param applicationInsightsConnectionString string
param imageTag string
@minLength(36)
param entraTenantId string
@minLength(36)
param entraClientId string
@minLength(1)
param allowedObjectIds array
param modelDeploymentName string
var commonEnv = [
  { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
  { name: 'Data__Mode', value: 'Azure' }
  { name: 'Cosmos__Endpoint', value: cosmosEndpoint }
  { name: 'Cosmos__Database', value: 'dse-growth-loop' }
  { name: 'Storage__BlobEndpoint', value: 'https://${storageName}.blob.${environment().suffixes.storage}/' }
  { name: 'Storage__QueueEndpoint', value: 'https://${storageName}.queue.${environment().suffixes.storage}/' }
  { name: 'KeyVault__Uri', value: keyVaultUri }
  { name: 'Foundry__Endpoint', value: foundryEndpoint }
  { name: 'Foundry__Deployment', value: modelDeploymentName }
  { name: 'Jev__Endpoint', value: 'https://api.typesafe.ai/v1/systemone' }
  { name: 'Jev__SecretName', value: 'jev-api-key' }
  { name: 'Auth__TenantId', value: entraTenantId }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
]
var allowedEnv = [for (id, i) in allowedObjectIds: { name: 'Auth__AllowedObjectIds__${i}', value: id }]
var workerEnv = concat(commonEnv, [{ name: 'AZURE_CLIENT_ID', value: workerClientId }])
resource api 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'dse-api'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${apiIdentityId}': {} } }
  properties: {
    managedEnvironmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'auto', allowInsecure: false }
      registries: [{ server: '${registryName}.azurecr.io', identity: apiIdentityId }]
    }
    template: {
      containers: [{
        name: 'api'
        image: '${registryName}.azurecr.io/dse-api:${imageTag}'
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: concat(commonEnv, [
          { name: 'AZURE_CLIENT_ID', value: apiClientId }
          { name: 'Auth__ClientId', value: entraClientId }
        ], allowedEnv)
        probes: [
          { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, initialDelaySeconds: 10, periodSeconds: 30 }
          { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080 }, initialDelaySeconds: 5, periodSeconds: 15 }
        ]
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}
resource worker 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'dse-worker'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${workerIdentityId}': {} } }
  properties: {
    managedEnvironmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: { activeRevisionsMode: 'Single', registries: [{ server: '${registryName}.azurecr.io', identity: workerIdentityId }] }
    template: {
      containers: [{ name: 'worker', image: '${registryName}.azurecr.io/dse-worker:${imageTag}', resources: { cpu: json('0.25'), memory: '0.5Gi' }, env: workerEnv }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}
// ダミー初期データの投入は明示的な手動実行。毎起動の上書きはしない。
resource seed 'Microsoft.App/jobs@2025-07-01' = {
  name: 'dse-seed-demo'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${workerIdentityId}': {} } }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 300
      replicaRetryLimit: 1
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [{ server: '${registryName}.azurecr.io', identity: workerIdentityId }]
    }
    template: {
      containers: [{ name: 'seed', image: '${registryName}.azurecr.io/dse-worker:${imageTag}', args: ['--seed-demo'], resources: { cpu: json('0.25'), memory: '0.5Gi' }, env: workerEnv }]
    }
  }
}
output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
