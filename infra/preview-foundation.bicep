targetScope = 'resourceGroup'

// Manual setup only, after the owner reviews the new RG/identity/RBAC/OIDC requirements.
// Delivery workflows never execute this template or change existing infrastructure.
resource environment 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: 'dse-preview-environment'
  location: 'japaneast'
  tags: { project: 'dse-growth-loop', purpose: 'shared-preview-foundation' }
  properties: {
    appLogsConfiguration: { destination: 'none' }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }
}
resource pull 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: 'dse-preview-pull'
  location: 'japaneast'
  tags: { project: 'dse-growth-loop', purpose: 'preview-image-pull-only' }
}
output environmentId string = environment.id
output pullIdentityId string = pull.id
output pullPrincipalId string = pull.properties.principalId
