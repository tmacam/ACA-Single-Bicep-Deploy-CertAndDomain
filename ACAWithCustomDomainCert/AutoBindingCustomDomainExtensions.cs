// #:sdk Aspire.AppHost.Sdk@13.0.1
// #:package Aspire.Hosting.Azure.AppContainers@13.1.0-preview.1.25578.2
// #:package Azure.Provisioning.Dns@1.0.0-beta.1

#pragma warning disable ASPIRECOMPUTE001
#pragma warning disable AZPROVISION001

using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;

namespace ACAWithCustomDomainCert;

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

        // Add the new custom domain.
        app.Configuration.Ingress.CustomDomains.Add(containerAppCustomDomain);

        app.ResourceVersion = "2025-07-01"; // BindingType:auto is only available for now in preview API versions from 2024-10 onwwards
        // TODO(tiagoa): PR to Azure.Provisioning to add 2025-07-01 as a known API version for ContainerApp

        // Finally, we need to create the Managed Certificate and bind it to the custom domain
        // Interestingly, this Managed Certificate is a child of the Container App Environment,
        // it is not a child of the Container App itself, even though the binding is done on the
        // Container App.

        // There must be an easier way to get the parent ContainerAppManagedEnvironment from the current Container App resource
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
        autoBindManagedCertificate.DependsOn.Add(app);
        infrastructure.Add(autoBindManagedCertificate);
        // dependsOn: [ containerApp, dnsRecordA, dnsAsuidTxtRecord ]
    }
}
#endregion