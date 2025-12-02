// #:sdk Aspire.AppHost.Sdk@13.0.1
// #:package Aspire.Hosting.Azure.AppContainers@13.1.0-preview.1.25578.2
// #:package Azure.Provisioning.Dns@1.0.0-beta.1

#pragma warning disable ASPIRECOMPUTE001
#pragma warning disable AZPROVISION001

using Aspire.Hosting;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Primitives;
using System.Net;
using Azure.Provisioning.Dns;

// TODOS:
// * Instead of AddAzureInfrastructure, model the custom domain as its own resource that implements AzureBicepResource.
// * Use Outputs and a ProvisioningOutputReference to resolve the CustomDomainVerificationid from the CAE resource instead of using an existing reference
// * When you use BicepOutputReference it implicitly creates the "dependson" relationship when the resources get provisioned.


// CustomDomain with auto binding Managed Certificate flow
// Dependencies:
//  - an existing DNS Zone in Azure DNS
//  - an CAE (Azure Container App Environment)
//
// The process is a follows:
//  1. Create the DNS records required for domain validation (TXT 'asuid' and A record pointing to CAE IP)
//  2. Create the Container App with the custom domain configured with bindingType:auto
//  3. Create the Managed Certificate and bind it to the custom domain.


var containerAppName = "myapp";
var dnsZoneName = "apps.tmacam.dev";
// the container app name doesn't NEED to match the leaf part of the FQDN but let's keep it simple, shall we?
var customDomainFqdn = $"{containerAppName}.{dnsZoneName}";

var builder = DistributedApplication.CreateBuilder(args);

var cae = builder.AddAzureContainerAppEnvironment("aspireContainerEnv");

// Setup DNS "infrastructure" to create the required DNS records for domain validation
// TODO(tmacam): ensure CAE is created before these records are created
var dnsRecordsForValidation = builder
    .AddAzureInfrastructure("custom-domain", infra =>
    {
        // We are going to create a few DNS records to prove we own the domain
        // and then create the custom domain binding in the Container App.
        // We need access to a DnsZone resource to host those records.
        var dnsZone = DnsZone.FromExisting("dnsZone");
        dnsZone.Name = dnsZoneName;
        infra.Add(dnsZone);

        // CAE verificationId and IP Address
        //
        // We are definining something akin to a Bicep module so, in this "scope", the CAE is an existing
        // resource we can refeer to.
        var containerAppEnvironment = (ContainerAppManagedEnvironment)cae.Resource.AddAsExistingResource(infra);
        var subscriptionCustomDomainVerificationId = containerAppEnvironment.CustomDomainConfiguration.CustomDomainVerificationId;
        var containerAppEnvironmentStaticIP = containerAppEnvironment.StaticIP;

        // TXT 'asuid' record is required and checked during containerApp deployment (due to configuration.ingress.customDomains)
        var dnsAsuidTxtRecord = new DnsTxtRecord("dnsAsuidTxtRecord")
        {
            Name = $"asuid.{containerAppName}", // Remember: not arbitrary, must be 'asuid.<your-app-name>'
            TtlInSeconds = 3600,
            Parent = dnsZone,
            TxtRecords = {
                new DnsTxtRecordInfo()
                {
                    Values = [ subscriptionCustomDomainVerificationId ]
                }
            }
        };
        infra.Add(dnsAsuidTxtRecord);

        // A record pointing to the CAE environment - required by the certificate auto-binding logic during cert creation and binding
        DnsARecord dnsRecordA = new(nameof(dnsRecordA))
        {
            Name = containerAppName, // Remember: not arbitrary, must be '<your-app-name>'
            TtlInSeconds = 3600,
            Parent = dnsZone,
            ARecords =
            {
                new DnsARecordInfo()
                {
                    Ipv4Address = containerAppEnvironmentStaticIP
                }
            },
        };
        infra.Add(dnsRecordA);
});


var app = builder.AddContainer(containerAppName, "mcr.microsoft.com/k8se/quickstart:latest")
    .WithComputeEnvironment(cae)
    .WithContainerName(containerAppName)  // I really want a pretty name to refer to
    .WithHttpsEndpoint(targetPort: 80)
    .WithExternalHttpEndpoints()
    .PublishAsAzureContainerApp((infra, app) =>
    {
        app.ConfigureAutoBindingCustomDomain(customDomainFqdn);
    })
    .WaitFor(dnsRecordsForValidation);


// Finally, we need to create the Managed Certificate and bind it to the custom domain

// resource managedCertificate 'Microsoft.App/managedEnvironments/managedCertificates@2024-10-02-preview' = {
//   name: 'cert-${fqdnAppDomainName}-${rgUniqueSuffix}'
//   parent: managedEnvironment
//   location: location
//   properties: {
//     subjectName: fqdnAppDomainName
//     domainControlValidation: 'HTTP'
//   }
//   dependsOn: [
//     containerApp, dnsRecordA, dnsAsuidTxtRecord
//   ]
// }


var managedCertificateInfra = builder
    .AddAzureInfrastructure("managed-certificate-infra", infra => {
        var containerAppEnvironment = (ContainerAppManagedEnvironment)cae.Resource.AddAsExistingResource(infra);

        ContainerAppManagedCertificate managedCert = new(nameof(managedCert))
        {
            Parent = containerAppEnvironment,
            Name = $"cert-{customDomainFqdn}",
            Properties = new() {
                SubjectName = customDomainFqdn,
                DomainControlValidation = new StringLiteralExpression("HTTP"),
            }  
        };
        infra.Add(managedCert);
    });



// Enact the deployment

builder.Build().Run();

// .. and we are done!

//
// The folowing code would eventually be replaced by a proper Azure.Provisioning SDK release
// with DNS zone and record support
//

#region  bindingType:auto extension method
public static class CustomDnsContainerAppExtensions
{
    // Ideally this method should be combined with ContainerAppExtensions::ConfigureCustomDomain so ir can handle all 3 cases
    public static void ConfigureAutoBindingCustomDomain(this ContainerApp app, /*IResourceBuilder<ParameterResource>*/ string customDomain)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(customDomain);

        if (app.ParentInfrastructure is not AzureResourceInfrastructure module)
        {
            throw new ArgumentException("Cannot configure custom domain when resource is not parented by ResourceModuleConstruct.", nameof(app));
        }

        var containerAppCustomDomain = new ContainerAppCustomDomain()
        {
            BindingType = new StringLiteralExpression("Auto"),
            Name = new StringLiteralExpression(customDomain), //customDomain.AsProvisioningParameter(module),
        };

        var existingCustomDomain = app.Configuration.Ingress.CustomDomains
            .FirstOrDefault(cd =>
            {
                // This is a cautionary tale to anyone who reads this code as to the dangers
                // of using implicit conversions in C#. BicepValue<T> uses some implicit conversions
                // which means we need to explicitly cast to IBicepValue so that we can get at the
                // source construct behind the Bicep value on the "name" field for a custom domain
                // in the Bicep. If the constructs are the same ProvisioningParameter then we have a
                // match - otherwise we are possibly dealing with a second domain. This deals with the
                // edge case of where someone might call ConfigureCustomDomain multiple times on the
                // same domain - unlikely but possible if someone has built some libraries.                
                var itemDomainNameBicepValue = cd.Value?.Name as IBicepValue;
                var candidateDomainNameBicepValue = containerAppCustomDomain.Name as IBicepValue;
                return itemDomainNameBicepValue?.Source?.Construct == candidateDomainNameBicepValue.Source?.Construct;
            });

        if (existingCustomDomain is not null)
        {
            app.Configuration.Ingress.CustomDomains.Remove(existingCustomDomain);
        }

        app.Configuration.Ingress.CustomDomains.Add(containerAppCustomDomain);

        app.ResourceVersion = "2025-07-01"; // BindingType:auto is only available for now in preview API versions from 2024-10 onwwards
    }
}
#endregion