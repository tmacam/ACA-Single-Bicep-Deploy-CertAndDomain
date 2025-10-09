@description('The location to deploy all my resources')
param location string = resourceGroup().location
param dnsZoneName string = 'apps.tmacam.dev'

// param dnsZoneResourceGroupName string = 'aca.tmacam.dev-dns'

var rgUniqueSuffix = uniqueString(resourceGroup().id)

var containerAppName = 'single-bicep-mcert-capp'
var fqdnAppDomainName = '${containerAppName}.${dnsZoneName}'

// Here is the thing: this probably needs to be created before this whole deployment
// because you need this zone properly configured if we trully want this to be done as a single
// deployment.
resource dnsZone 'Microsoft.Network/dnsZones@2023-07-01-preview' = {
  name: dnsZoneName
  location: 'global'
}


// Container Apps Environment
resource managedEnvironment 'Microsoft.App/managedEnvironments@2025-02-02-preview' = {
  name: 'cae-${rgUniqueSuffix}'
  location: location
  properties: {
    workloadProfiles : [
      {
        workloadProfileType: 'E4'
        name: 'smallwp'
        maximumCount: 1
        minimumCount: 1
      }
    ]
  }
}

// The Custom Domain Verification ID is a subscription-level constant value. We are retriving it
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
// Container App with custom domain and auto-managed certificate
//

// `auto` bindingType is supported from 2024-10-02-preview onwards. This is still in preview, so please use with care.
resource containerApp 'Microsoft.App/containerApps@2024-10-02-preview' = {
  name: 'app-${rgUniqueSuffix}'
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
          bindingType: 'auto' // <<<<< Magic happens here!
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
    dnsAsuidTxtRecord, dnsRecordA
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


output nameServers array = dnsZone.properties.nameServers
output containerAppUrl string = containerApp.properties.configuration.ingress.fqdn
output managedCertificateId string = managedCertificate.id

output subscriptionCustomDomainVerificationId string = subscriptionCustomDomainVerificationId
