param location string
param customDomain string
param managedEnvironmentName string
param managedCertName string

resource existingManagedEnvironment 'Microsoft.App/managedEnvironments@2024-10-02-preview' existing = {
  name: managedEnvironmentName
  scope: resourceGroup()
}

resource managedCertificate 'Microsoft.App/managedEnvironments/managedCertificates@2024-10-02-preview' = {
  parent: existingManagedEnvironment
  name: managedCertName
  location: location
  properties: {
    subjectName: customDomain
    domainControlValidation: 'HTTP'
  }
}
