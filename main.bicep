@description('The DNS zone name to use for the Container App custom domain')
param dnsZoneName string = 'apps.tmacam.dev'

@description('The name of the Container App to create')
param containerAppName string = 'single-bicep-mcert-capp'

@description('The location to deploy all my resources')
param location string = resourceGroup().location

param containerAppEnvironmentName string = 'aspirecontainerenv76lcyi'


// We could and probably should retrieve this (or parts of this) from a property of existing resources
// to make bicep track inter-resources dependencies for us. But honestly, this is simpler to understand.
var fqdnAppDomainName = '${containerAppName}.${dnsZoneName}'

// not really required but nice to have unique names
var rgUniqueSuffix = uniqueString(resourceGroup().id)


//
// DNS Zone - existing
//
// Here is the thing: to make this really a single-deploy setup this needs to be created in advance.
// You need this zone properly configured (i.e., have its NS servers pointing to the right place)
//  if we truly want this to be done as a single  deployment.
resource dnsZone 'Microsoft.Network/dnsZones@2023-07-01-preview' existing = {
  name: dnsZoneName
}

//
// CAE and Container App
//


// Container Apps Environment
//
// Using an existing one for simplicity.
// Note: CAE must be in a region that supports it and also supports managed certificates
resource managedEnvironment 'Microsoft.App/managedEnvironments@2025-02-02-preview' existing = {
    name: containerAppEnvironmentName
}

// The Custom Domain Verification ID is a subscription-level constant value. We are retrieving it
// from the CAE because it is handily available here. You could retrieve it from a GET call to
// az rest --method get --url "https://management.azure.com/subscriptions/%3CsubscriptionId%3E/providers/Microsoft.Resources?api-version=2021-04-01"
var subscriptionCustomDomainVerificationId  = managedEnvironment.properties.customDomainConfiguration.customDomainVerificationId

//
// DNS entries required for DNS ownership validation.
//
// Notice: the `name` fields for the DNS records MATTER! Those are the names of the DNS recods stripped of the parent DNS zone name.
// So if your DNS zone is apps.tmacam.dev, and you want to create a record for asuid.single-bicep-mcert-capp.apps.tmacam.dev
// the name of the record is asuid.single-bicep-mcert-capp!

// TXT 'asuid' record is required and checked during containerApp deployment (due to configuration.ingress.customDomains)
resource dnsAsuidTxtRecord 'Microsoft.Network/dnsZones/TXT@2023-07-01-preview' = {
  name: 'asuid.${containerAppName}' // Remember: not arbitrary, must be 'asuid.<your-app-name>'
  parent: dnsZone
  properties: {
    TTL: 3600
    TXTRecords: [
      {
        value:[ subscriptionCustomDomainVerificationId ]
      }        
    ]       
  }
}

// A record pointing to the CAE environment - required by the certificate auto-binding logic during cert creation and binding
resource dnsRecordA 'Microsoft.Network/dnsZones/A@2023-07-01-preview' = {
  name: containerAppName // Remember: not arbitrary, must be '<your-app-name>'
  parent: dnsZone
  properties: {
    TTL: 3600
    ARecords: [
      {
        ipv4Address: managedEnvironment.properties.staticIp
      }
    ]
  }
}

//
// Dns Resolvability
//
// This is a bit of a hack. The DNS resolution might not be available immediately after the DNS records are created.
// and it can take up to 60s for the DNS resolution to be available. To ensure that the DNS resolution is available
// before the container app is created, we use a simple loop to check the DNS resolution.
resource waitForDnsToBeResolvable 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: 'waitForDnsToBeResolvable-${containerAppName}'
  kind: 'AzurePowerShell'
  location: location
  properties: {
    azPowerShellVersion: '14.0'
    cleanupPreference: 'Always'
    retentionInterval: 'P1D'
    timeout: 'PT2M'
    scriptContent: 'Start-Sleep -Seconds 60; exit 0'
  }
  dependsOn: [
    dnsAsuidTxtRecord, dnsRecordA
  ]
}


//
// Container App with custom domain and auto-managed certificate
//

// `auto` bindingType is supported from 2024-10-02-preview onwards. This is still in preview, so please use with care.
resource containerApp 'Microsoft.App/containerApps@2024-10-02-preview' = {
  name: 'app-${containerAppName}'
  location: location
  properties: {
    managedEnvironmentId: managedEnvironment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 80
        allowInsecure: false
        customDomains: [{
          name: fqdnAppDomainName
          bindingType: 'auto' // <<<<< ✨ Magic happens here!
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
  dependsOn: [
    dnsAsuidTxtRecord, dnsRecordA, waitForDnsToBeResolvable
  ]
}



resource managedCertificate 'Microsoft.App/managedEnvironments/managedCertificates@2024-10-02-preview' = {
  name: 'cert-${fqdnAppDomainName}-${rgUniqueSuffix}'
  parent: managedEnvironment
  location: location
  properties: {
    subjectName: fqdnAppDomainName
    domainControlValidation: 'HTTP'
  }
  dependsOn: [
    containerApp, dnsRecordA, dnsAsuidTxtRecord
  ]
}

// ... and we are done!

//
// Outputs
//


output nameServers array = dnsZone.properties.nameServers
output containerAppUrl string = containerApp.properties.configuration.ingress.fqdn
output managedCertificateId string = managedCertificate.id
output subscriptionCustomDomainVerificationId string = subscriptionCustomDomainVerificationId
