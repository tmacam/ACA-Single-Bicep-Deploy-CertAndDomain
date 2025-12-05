// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;

namespace ACAWithCustomDomainCert;

public static class AutoBindingCustomDomainExtensions
{
    // Configures a ContainerApp to use a custom domain with auto binding managed certificate - along with the required resources for that.
    public static void ConfigureAutoBindingCustomDomain(
        this ContainerApp app,
        IResourceBuilder<AzureContainerAppEnvironmentResource> cae,
        AzureResourceInfrastructure containerAppInfrastructure,
        string hostname,
        string dnsDomain)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(cae);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        ArgumentException.ThrowIfNullOrWhiteSpace(dnsDomain);

        string customDomainFqdn = $"{hostname}.{dnsDomain}";

        if (app.ParentInfrastructure is not AzureResourceInfrastructure module)
        {
            throw new ArgumentException("Cannot configure custom domain when resource is not parented by ResourceModuleConstruct.", nameof(app));
        }

        // CustomDomain with auto binding Managed Certificate flow
        // Dependencies:
        //  - an existing DNS Zone in Azure DNS. We will create the required DNS records there.
        //  - an CAE (Azure Container App Environment). We need its static IP and its CustomDomainVerificationId
        //
        // The process is a follows:
        //  1. Create the DNS records required for domain ownership validation (TXT 'asuid' and A record pointing to CAE IP)
        //  2. Create/Configure the Container App with the custom domain configured with bindingType:auto
        //  3. Create the Managed Certificate and bind it to the custom domain.

        // Step 1:
        // DNS Ownership Verification Resources
        AzureDnsOwnershipVerificationResourceExtension.GetVerificationIdAndStaticIP(
            cae,
            containerAppInfrastructure,
            out var subscriptionCustomDomainVerificationId,
            out var containerAppEnvironmentStaticIP);
        (var dnsAsuidTxtRecord, var dnsRecordA) = AzureDnsOwnershipVerificationResourceExtension.ConfigureInfrastructure(
            hostname,
            dnsDomain,
            subscriptionCustomDomainVerificationId,
            containerAppEnvironmentStaticIP,
            containerAppInfrastructure);
        // Deploy DNS Verification Resources with the Container App Infrastructure,
        // But also make sure that they are deployed _before_ the Container App itself
        app.DependsOn.Add(dnsAsuidTxtRecord); // CustomDomain+auto binding requires the TXT record to be present
        app.DependsOn.Add(dnsRecordA); // Just to be on the safe side, only the managed certs requires this A record to be present

        // Step 2:
        // Configure the custom domain on the Container App with bindingType:auto
        var containerAppCustomDomain = new ContainerAppCustomDomain()
        {
            BindingType = new StringLiteralExpression("Auto"),
            Name = new StringLiteralExpression(customDomainFqdn), // Needs to be the fully qualified domain name, not only the hostname.
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
        // BindingType:auto is only available for now in preview API versions from 2024-10 onwwards,
        // so we need to update the resource version of this container app.
        app.ResourceVersion = "2025-07-01";
        // TODO(tiagoa): PR to Azure.Provisioning to add 2025-07-01 as a known API version for ContainerApp

        // Step 3:
        // Finally, we need to create the Managed Certificate and bind it to the custom domain
        // Interestingly, this Managed Certificate is a child of the Container App Environment,
        // it is not a child of the Container App itself, even though the binding is done on the
        // Container App. Cray-zey.
        // There must be an easier way to get the parent ContainerAppManagedEnvironment from the current Container App resource
        ContainerAppManagedEnvironment containerAppEnvironment = (ContainerAppManagedEnvironment)cae.Resource.AddAsExistingResource(containerAppInfrastructure);
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
        autoBindManagedCertificate.DependsOn.Add(dnsRecordA); // The A record is required for cert binding. It is checked during cert creation..
        containerAppInfrastructure.Add(autoBindManagedCertificate);
    }
}