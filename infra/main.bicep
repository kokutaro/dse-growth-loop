targetScope = 'subscription'

@allowed(['dse-growth-loop-rg'])
param resourceGroupName string = 'dse-growth-loop-rg'
param location string = 'japaneast'
param foundryLocation string = location
param deployApplications bool = false
param imageTag string = 'initial'
param entraTenantId string = ''
param entraClientId string = ''
param allowedObjectIds array = []
param modelName string = ''
param modelVersion string = ''
param modelDeploymentName string = ''
param modelSku string = 'GlobalStandard'
@minValue(1)
param modelCapacity int = 1

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
  }
}

module apps 'modules/apps.bicep' = if (deployApplications) {
  name: 'dse-apps'
  scope: rg
  params: {
    location: location
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
