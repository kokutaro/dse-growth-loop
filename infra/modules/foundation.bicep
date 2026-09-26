param location string
param foundryLocation string
param modelName string
param modelVersion string
param modelDeploymentName string
param modelSku string
param modelCapacity int
param approvedMonthlyBudgetJpy int
param budgetEmails array
param budgetStartDate string
param logsDailyQuotaGb string
param logsRetentionDays int
param telemetrySamplingPercentage int
param blobSoftDeleteDays int
param blobVersioningEnabled bool
param blobHistoryReviewDays int
param acrKeepLatestTags int
param acrMinimumImageAgeDays int

// 通知だけ。停止・削除アクションは登録しない。請求通貨JPYの契約で使用する。
resource budget 'Microsoft.Consumption/budgets@2024-08-01' = if (approvedMonthlyBudgetJpy > 0) {
  name: 'dse-development-monthly'
  properties: {
    amount: approvedMonthlyBudgetJpy
    category: 'Cost'
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStartDate }
    notifications: {
      actual80: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 80, thresholdType: 'Actual', contactEmails: budgetEmails }
      actual100: { enabled: true, operator: 'GreaterThanOrEqualTo', threshold: 100, thresholdType: 'Actual', contactEmails: budgetEmails }
    }
  }
}

var suffix = uniqueString(resourceGroup().id)
var tags = { project: 'dse-growth-loop', purpose: 'dummy-data-demo' }

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'dse-logs-${suffix}'
  location: location
  tags: tags
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: logsRetentionDays, workspaceCapping: { dailyQuotaGb: json(logsDailyQuotaGb) } }
}
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'dse-insights-${suffix}'
  location: location
  kind: 'web'
  tags: tags
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id, IngestionMode: 'LogAnalytics', SamplingPercentage: telemetrySamplingPercentage }
}
resource environment 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: 'dse-environment'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: { customerId: logs.properties.customerId, sharedKey: logs.listKeys().primarySharedKey }
    }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }
}
resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: 'dseacr${suffix}'
  location: location
  tags: union(tags, { keepLatestTags: string(acrKeepLatestTags), minimumImageAgeDays: string(acrMinimumImageAgeDays), cleanupMode: 'Manual' })
  // 手動レビュー用の保持方針。自動purgeは作成しない。
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false, anonymousPullEnabled: false }
}
resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' = {
  name: 'dsest${suffix}'
  location: location
  tags: union(tags, { historyReviewDays: string(blobHistoryReviewDays), cleanupMode: 'Manual' })
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: { minimumTlsVersion: 'TLS1_2', supportsHttpsTrafficOnly: true, allowBlobPublicAccess: false, allowSharedKeyAccess: false, defaultToOAuthAuthentication: true }
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2025-01-01' = {
  parent: storage
  name: 'default'
  properties: { isVersioningEnabled: blobVersioningEnabled, deleteRetentionPolicy: { enabled: true, days: blobSoftDeleteDays } }
}
resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-01-01' = [for name in ['originals', 'extracted']: {
  parent: blobs
  name: name
  properties: { publicAccess: 'None' }
}]
resource queues 'Microsoft.Storage/storageAccounts/queueServices@2025-01-01' = { parent: storage, name: 'default', properties: {} }
resource jobQueues 'Microsoft.Storage/storageAccounts/queueServices/queues@2025-01-01' = [for name in ['ingestion', 'ingestion-poison']: {
  parent: queues
  name: name
  properties: {}
}]
resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2025-04-15' = {
  name: 'dse-cosmos-${suffix}'
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    capabilities: [{ name: 'EnableServerless' }]
    locations: [{ locationName: location, failoverPriority: 0, isZoneRedundant: false }]
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    disableLocalAuth: true
    minimalTlsVersion: 'Tls12'
    publicNetworkAccess: 'Enabled'
  }
}
resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2025-04-15' = {
  parent: cosmos
  name: 'dse-growth-loop'
  properties: { resource: { id: 'dse-growth-loop' } }
}
resource catalog 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2025-04-15' = {
  parent: database
  name: 'catalog'
  properties: {
    resource: {
      id: 'catalog'
      partitionKey: { paths: ['/tenantId'], kind: 'Hash', version: 2 }
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [{ path: '/*' }]
        excludedPaths: [{ path: '/summary/?' }, { path: '/sourceRefs/*' }, { path: '/"_etag"/?' }]
      }
    }
  }
}
resource conversations 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2025-04-15' = {
  parent: database
  name: 'conversations'
  properties: { resource: { id: 'conversations', partitionKey: { paths: ['/sessionId'], kind: 'Hash', version: 2 } } }
}
resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: 'dse-kv-${suffix}'
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true
    accessPolicies: []
  }
}
resource foundry 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: 'dse-foundry-${suffix}'
  location: foundryLocation
  tags: tags
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: { customSubDomainName: 'dse-foundry-${suffix}', allowProjectManagement: true, disableLocalAuth: true, publicNetworkAccess: 'Enabled' }
}
resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: foundry
  name: 'dse-growth-loop'
  location: foundryLocation
  identity: { type: 'SystemAssigned' }
  properties: { displayName: 'DSE Growth Loop', description: 'ダミーデータによる検索検証' }
}
resource model 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = if (!empty(modelDeploymentName)) {
  parent: foundry
  name: modelDeploymentName
  sku: { name: modelSku, capacity: modelCapacity }
  properties: { model: { format: 'OpenAI', name: modelName, version: modelVersion }, versionUpgradeOption: 'NoAutoUpgrade' }
}
resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = [for name in ['api', 'worker']: {
  name: 'dse-${name}-identity'
  location: location
  tags: tags
}]
resource pullRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (name, i) in ['api', 'worker']: {
  name: guid(registry.id, identities[i].id, 'AcrPull')
  scope: registry
  properties: { principalId: identities[i].properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d') }
}]
// API/Workerとも業務更新とフェンスの条件付き書込が必要。DB全体には付与しない。
var cosmosAccess = [
  { identity: 0, container: 'catalog' }
  { identity: 0, container: 'conversations' }
  { identity: 1, container: 'catalog' }
  { identity: 1, container: 'conversations' }
]
resource cosmosRoles 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2025-04-15' = [for grant in cosmosAccess: {
  parent: cosmos
  name: guid(cosmos.id, identities[grant.identity].id, grant.container)
  properties: {
    principalId: identities[grant.identity].properties.principalId
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: '${cosmos.id}/dbs/${database.name}/colls/${grant.container}'
  }
}]
// #6の原本受付/抽出、#8の原本参照に必要なコンテナだけに限定。
resource apiOriginalsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containers[0].id, identities[0].id, 'blob')
  scope: containers[0]
  properties: { principalId: identities[0].properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe') }
}
resource workerBlobRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in range(0, 2): {
  name: guid(containers[i].id, identities[1].id, 'blob')
  scope: containers[i]
  properties: { principalId: identities[1].properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', i == 0 ? '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1' : 'ba92f5b4-2d11-453d-a403-e96b0029c9fe') }
}]
resource apiQueueSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(jobQueues[0].id, identities[0].id, 'sender')
  scope: jobQueues[0]
  properties: { principalId: identities[0].properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'c6a89b2d-59bc-44d0-9896-0f6e12d7b80a') }
}
resource workerQueueRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for i in range(0, 2): {
  name: guid(jobQueues[i].id, identities[1].id, 'queue')
  scope: jobQueues[i]
  properties: { principalId: identities[1].properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', i == 0 ? '974c5e8b-45b9-4653-ba55-5f855dd0fb88' : 'c6a89b2d-59bc-44d0-9896-0f6e12d7b80a') }
}]
resource vaultRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (name, i) in ['api', 'worker']: {
  name: guid(vault.id, identities[i].id, 'secrets')
  scope: vault
  properties: { principalId: identities[i].properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') }
}]
resource foundryRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (name, i) in ['api', 'worker']: {
  name: guid(foundry.id, identities[i].id, 'inference')
  scope: foundry
  properties: { principalId: identities[i].properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd') }
}]

output environmentId string = environment.id
output registryName string = registry.name
output apiIdentityId string = identities[0].id
output apiClientId string = identities[0].properties.clientId
output workerIdentityId string = identities[1].id
output workerClientId string = identities[1].properties.clientId
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output storageName string = storage.name
output keyVaultUri string = vault.properties.vaultUri
output foundryEndpoint string = 'https://${foundry.name}.openai.azure.com/'
output applicationInsightsConnectionString string = insights.properties.ConnectionString
