@description('The location to deploy all my resources')
// This change is currently only in northcentralus(stage). We will be deploying to all Prod regions starting Q1 2025.
param location string = 'northcentralus(stage)'

var containerAppName = 'single-bicep-mcert-capp'
var customDomain = 'single-bicep-custom-domain.tdaroly-dev.com'
var managedEnvName = 'managedEnvironment-playground-a5a5'
var managedCertName = 'singlebicepcert'

// `auto` bindingType is supported from 2024-10-02-preview onwards. This is still in preview, so please use with care.
resource containerApp 'Microsoft.App/containerApps@2024-10-02-preview' = {
  name: containerAppName
  location: location
  properties: {
    managedEnvironmentId: resourceId('Microsoft.App/managedEnvironments', managedEnvName)
    configuration: {
      ingress: {
        external: true
        targetPort: 80
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
        customDomains: [{
          name: customDomain
          bindingType: 'auto'
        }]
      }
    }
    template: {
      containers: [
        {
          name: containerAppName
          image: 'mcr.microsoft.com/k8se/quickstart:latest'
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 3
      }
    }
  }
}

module managedCertificateModule 'managedCertificate.bicep' = {
  name: managedCertName
  scope: resourceGroup('playground')
  params: {
    location: location
    customDomain: customDomain
    managedEnvironmentName: managedEnvName
    managedCertName: managedCertName
  }
  dependsOn: [
    containerApp
  ]
}
