@description('The location to deploy all my resources')
param location string = resourceGroup().location
param dnsZoneName string = 'apps.tmacam.dev'

// param dnsZoneResourceGroupName string = 'aca.tmacam.dev-dns'

var rgUniqueSuffix = uniqueString(resourceGroup().id)

var containerAppName = 'single-bicep-mcert-capp'
var fqdnAppDomainName = '${containerAppName}.${dnsZoneName}'


resource dnsZone 'Microsoft.Network/dnsZones@2023-07-01-preview' = {
  name: dnsZoneName
  location: 'global'
}


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

resource iOnlyExistToProvideACustomDomainVerificationId 'Microsoft.App/containerApps@2024-10-02-preview' = {
  name: 'app-for-suid-${rgUniqueSuffix}'
  location: location
  properties: {
    managedEnvironmentId: managedEnvironment.id
    configuration: {
      ingress: {
        external: false
        targetPort: 80
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: containerAppName
          image: 'mcr.microsoft.com/k8se/quickstart:latest'
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}


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
    dnsAsuidTxtRecord
  ]
}

// resource dnsZoneResourceGroup 'Microsoft.Resources/resourceGroups@2021-04-01' existing = {
//   name: dnsZoneResourceGroupName
//   scope: subscription( )
// }

// module dnsAsuidTxtRecord 'dnsAsuidTxtRecord.bicep' = {
//   name: 'asuid-module-${fqdnAppDomainName}'
//   params: {
//     dnsZoneName: dnsZoneName
//     asuidTxtRecordName: ionlyExistToProvideACustomDomainVerificationId.properties.customDomainVerificationId
//     TTL: 3600
//     containerAppName: containerAppName
//   }
// }

// for DNS validation...
resource dnsAsuidTxtRecord 'Microsoft.Network/dnsZones/TXT@2023-07-01-preview' = {
  name: 'asuid.${containerAppName}' 
  parent: dnsZone
  properties: {
    TTL: 3600
    TXTRecords: [
      {
        value:[ iOnlyExistToProvideACustomDomainVerificationId.properties.customDomainVerificationId ]
      }        
    ]       
  }
}

// A record pointing to tp the CAE environment
resource dnsRecordA 'Microsoft.Network/dnsZones/A@2023-07-01-preview' = {
  name: containerAppName
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
output subscriptionCustomDomainVerificationId string = managedEnvironment.properties.customDomainConfiguration.customDomainVerificationId
