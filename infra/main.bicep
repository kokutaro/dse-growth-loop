targetScope = 'subscription'

@allowed(['dse-growth-loop-rg'])
param resourceGroupName string = 'dse-growth-loop-rg'
param location string = 'japaneast'
param foundryLocation string = location
param deployApplications bool = false
param imageTag string = 'initial'
param entraTenantId string = ''
param entraClientId string = ''
@maxLength(1)
param allowedObjectIds array = []
param modelName string = ''
param modelVersion string = ''
param modelDeploymentName string = ''
param modelSku string = 'GlobalStandard'
@minValue(1)
param modelCapacity int = 1

param deployIngestionWorker bool = false
// 配置も手動分析の実行も別承認。定期トリガーは定義しない。
param deployBusinessAnalysisJob bool = false
param runtimeSettings array = [
  { name: 'AiUsage__Stopped', value: 'true' }
  { name: 'Operations__AnalysisMode', value: 'Manual' }
  { name: 'Operations__RecoveryMode', value: 'Manual' }
  { name: 'Operations__WarmupEnabled', value: 'false' }
  { name: 'Logging__LogLevel__Default', value: 'Warning' }
]
@minValue(0)
param approvedMonthlyBudgetJpy int = 0
param budgetEmails array = []
param budgetStartDate string = ''
param logsDailyQuotaGb string = '0.05'
@minValue(30)
@maxValue(730)
param logsRetentionDays int = 30
@minValue(1)
@maxValue(100)
param telemetrySamplingPercentage int = 10
@minValue(1)
@maxValue(365)
param blobSoftDeleteDays int = 7
param blobVersioningEnabled bool = true
@minValue(1)
param blobHistoryReviewDays int = 30
@minValue(1)
param acrKeepLatestTags int = 3
@minValue(1)
param acrMinimumImageAgeDays int = 30

resource rg 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: resourceGroupName
  location: location
  tags: { project: 'dse-growth-loop', purpose: 'dummy-data-demo' }
}

module foundation 'modules/foundation.bicep' = {
  name: 'dse-foundation'
  scope: rg
  params: {
    location: location
    foundryLocation: foundryLocation
    modelName: modelName
    modelVersion: modelVersion
    modelDeploymentName: modelDeploymentName
    modelSku: modelSku
    modelCapacity: modelCapacity
    approvedMonthlyBudgetJpy: approvedMonthlyBudgetJpy
    budgetEmails: budgetEmails
    budgetStartDate: budgetStartDate
    logsDailyQuotaGb: logsDailyQuotaGb
    logsRetentionDays: logsRetentionDays
    telemetrySamplingPercentage: telemetrySamplingPercentage
    blobSoftDeleteDays: blobSoftDeleteDays
    blobVersioningEnabled: blobVersioningEnabled
    blobHistoryReviewDays: blobHistoryReviewDays
    acrKeepLatestTags: acrKeepLatestTags
    acrMinimumImageAgeDays: acrMinimumImageAgeDays
  }
}

module apps 'modules/apps.bicep' = if (deployApplications) {
  name: 'dse-apps'
  scope: rg
  params: {
    location: location
    deployIngestionWorker: deployIngestionWorker
    deployBusinessAnalysisJob: deployBusinessAnalysisJob
    runtimeSettings: runtimeSettings
    telemetrySamplingPercentage: telemetrySamplingPercentage
    environmentId: foundation.outputs.environmentId
    registryName: foundation.outputs.registryName
    apiIdentityId: foundation.outputs.apiIdentityId
    apiClientId: foundation.outputs.apiClientId
    workerIdentityId: foundation.outputs.workerIdentityId
    workerClientId: foundation.outputs.workerClientId
    cosmosEndpoint: foundation.outputs.cosmosEndpoint
    storageName: foundation.outputs.storageName
    keyVaultUri: foundation.outputs.keyVaultUri
    foundryEndpoint: foundation.outputs.foundryEndpoint
    applicationInsightsConnectionString: foundation.outputs.applicationInsightsConnectionString
    imageTag: imageTag
    entraTenantId: entraTenantId
    entraClientId: entraClientId
    allowedObjectIds: allowedObjectIds
    modelDeploymentName: modelDeploymentName
  }
}

output resourceGroup string = rg.name
output registryName string = foundation.outputs.registryName
output keyVaultUri string = foundation.outputs.keyVaultUri
output cosmosEndpoint string = foundation.outputs.cosmosEndpoint
output foundryEndpoint string = foundation.outputs.foundryEndpoint
output apiUrl string = deployApplications ? apps!.outputs.apiUrl : ''
