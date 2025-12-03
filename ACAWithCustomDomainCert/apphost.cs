// #:sdk Aspire.AppHost.Sdk@13.0.1
// #:package Aspire.Hosting.Azure.AppContainers@13.1.0-preview.1.25578.2
// #:package Azure.Provisioning.Dns@1.0.0-beta.1

#pragma warning disable ASPIRECOMPUTE001
#pragma warning disable AZPROVISION001

using ACAWithCustomDomainCert;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;

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
var dnsVerificationRecods = builder.AddAzureDnsOwnershipVerificationResource(
    name: "dnsOwnershipVerification",
    hostname: containerAppName,
    dnsDomain: dnsZoneName,
    cae: cae);

var app = builder.AddContainer(containerAppName, "mcr.microsoft.com/k8se/quickstart:latest")
    .WithComputeEnvironment(cae)
    .WithContainerName(containerAppName)  // I really want a pretty name to refer to
    .WithHttpsEndpoint(targetPort: 80)
    .WithExternalHttpEndpoints()
    .PublishAsAzureContainerApp((infra, app) =>
    {
        app.ConfigureAutoBindingCustomDomain(cae, infra, customDomainFqdn);
    })
    .WaitFor(dnsVerificationRecods);

// Enact the deployment
builder.Build().Run();

// .. and we are done!

//
// The folowing code would eventually be replaced by a proper Azure.Provisioning SDK release
// with DNS zone and record support
//

#region  bindingType:auto extension method
public static class AutoBindingCustomDomainExtensions
{
    // Ideally this method should be combined with ContainerAppExtensions::ConfigureCustomDomain so ir can handle all 3 cases
    public static void ConfigureAutoBindingCustomDomain(
        this ContainerApp app,
        IResourceBuilder<AzureContainerAppEnvironmentResource> cae,
        AzureResourceInfrastructure infrastructure,
         /*IResourceBuilder<ParameterResource>*/ string customDomainFqdn)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(cae);
        ArgumentException.ThrowIfNullOrWhiteSpace(customDomainFqdn);

        // 1. Configure the custom domain on the Container App with bindingType:auto

        if (app.ParentInfrastructure is not AzureResourceInfrastructure module)
        {
            throw new ArgumentException("Cannot configure custom domain when resource is not parented by ResourceModuleConstruct.", nameof(app));
        }

        var containerAppCustomDomain = new ContainerAppCustomDomain()
        {
            BindingType = new StringLiteralExpression("Auto"),
            Name = new StringLiteralExpression(customDomainFqdn), //customDomain.AsProvisioningParameter(module),
        };

        // Remove any existing custom domain with the same name to avoid duplicates
        var candidateDomainNameBicepValue = containerAppCustomDomain.Name as IBicepValue;
        app.Configuration.Ingress.CustomDomains
            .Where(cd =>
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
                return itemDomainNameBicepValue?.Source?.Construct == candidateDomainNameBicepValue.Source?.Construct;
            })
            .ToList() // Materialize to avoid modifying collection during enumeration
            .ForEach(i => app.Configuration.Ingress.CustomDomains.Remove(i));
        // We are safe now. Add the new custom domain.
        app.Configuration.Ingress.CustomDomains.Add(containerAppCustomDomain);

        app.ResourceVersion = "2025-07-01"; // BindingType:auto is only available for now in preview API versions from 2024-10 onwwards
        // TODO(tiagoa): PR to Azure.Provisioning to add 2025-07-01 as a known API version for ContainerApp

        // Finally, we need to create the Managed Certificate and bind it to the custom domain
        // Interestingly, this Managed Certificate is a child of the Container App Environment,
        // it is not a child of the Container App itself, even though the binding is done on the
        // Container App.

        ContainerAppManagedEnvironment containerAppEnvironment = (ContainerAppManagedEnvironment)cae.Resource.AddAsExistingResource(infrastructure);
        ContainerAppManagedCertificate autoBindManagedCertificate = new(nameof(autoBindManagedCertificate))
        {
            Parent = containerAppEnvironment,
            Name = customDomainFqdn,
            Properties = new()
            {
                SubjectName = customDomainFqdn,
                DomainControlValidation = new StringLiteralExpression("HTTP"),
            }
        };
        infrastructure.Add(autoBindManagedCertificate);
        // dependsOn: [ containerApp, dnsRecordA, dnsAsuidTxtRecord ]
    }
}
#endregion