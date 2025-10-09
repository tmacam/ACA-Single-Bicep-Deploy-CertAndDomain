// this needs to be a different module because the DNZ Zoe is in a different resource group
// and Bicep doesn't support cross-resource-group resource dependencies in the same file

param dnsZoneName string
param asuidTxtRecordName string
param containerAppName string
param TTL int = 3600

// add reference to existing DNZ Zone in resouce group aca.tmacam.dev-dns
resource existingDnsZone 'Microsoft.Network/dnsZones@2023-07-01-preview' existing = {
  name: dnsZoneName
}

resource dnsRecordTXT 'Microsoft.Network/dnsZones/TXT@2023-07-01-preview' = {
  name: 'asuid.${containerAppName}' 
  parent: existingDnsZone
  properties: {
    TTL: TTL
    TXTRecords: [
      {
        value:[ asuidTxtRecordName ]
      }        
    ]       
  }
}
