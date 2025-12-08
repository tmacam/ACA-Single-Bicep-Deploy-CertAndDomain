// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Dns;
using Azure.Provisioning.Expressions;


namespace ACAWithCustomDomainCert;

#pragma warning disable AZPROVISION001 // DNS Provisioning is still in beta


public static class AutoBindingCustomDomainExtensions
{
    // Configures a ContainerApp to use a custom domain with auto binding managed certificate - along with the required resources for that.
    //
    // NOTICE about DNS record creation and race conditions: A successful ARM/Bicep deployment of
    // an Azure DNS record only guarantees the Control Plane (CP) has accepted and stored the record.
    // It does not guarantee is has been processed by DNS data plane (DP), nor that DNS resolution right
    // after a successful deployment will succeed.
    // While this deployment solution should work as an one-shot deployment, there is a small
    // window where DNS resolution might fail, which will cause the deployment to fail with a
    // message like this:
    //
    //     Deployment failed:
    //          InvalidCustomHostNameValidation: A TXT record pointing from asuid.{hostname}.{dnsZone}. to
    //          {subscriptionCustomDomainVerificationId} was not found.
    //
    // This is a known limitation of Azure DNS and is not something that can be fixed by this
    // code. The user should be aware of this and take appropriate actions (retries) if necessary.
    public static void ConfigureAutoBindingCustomDomain(
        this ContainerApp app,
        string hostname,
        string dnsDomain,
        AzureContainerAppEnvironmentResource caeResource,
        AzureResourceInfrastructure infrastructure)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(caeResource);
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

        // CAE verificationId and ContainerApp (environment) IP Address
        //
        // Think of AzureResourceInfrastructure as a Bicep module. We are defining
        // a reference to an existing Container App Environment resource in this "scope"
        // so we have a resource we can refer to.
        ContainerAppManagedEnvironment containerAppEnvironment = (ContainerAppManagedEnvironment)caeResource.AddAsExistingResource(infrastructure);
        BicepValue<string> subscriptionCustomDomainVerificationId = containerAppEnvironment.CustomDomainConfiguration.CustomDomainVerificationId;
        BicepValue<IPAddress> containerAppEnvironmentStaticIP = containerAppEnvironment.StaticIP;

        // Step 1:
        // DNS Ownership Verification Resources
        // Create and deploy the DNS records required for domain ownership validation        
        // with the Container App Infrastructure
        (var dnsAsuidTxtRecord, var dnsRecordA) = CreateDnsOwnershipInfrastructure(
            hostname,
            dnsDomain,
            subscriptionCustomDomainVerificationId,
            containerAppEnvironmentStaticIP,
            infrastructure);
        // Also make sure that they are deployed _before_ the Container App itself
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
        infrastructure.Add(autoBindManagedCertificate);
    }

    public static (DnsTxtRecord, DnsARecord) CreateDnsOwnershipInfrastructure(
        string hostname,
        string dnsDomain,
        BicepValue<string> subscriptionCustomDomainVerificationId,
        BicepValue<IPAddress> containerAppEnvironmentStaticIP,
        Infrastructure infrastructure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname, nameof(hostname));
        ArgumentException.ThrowIfNullOrWhiteSpace(dnsDomain, nameof(dnsDomain));
        ArgumentNullException.ThrowIfNull(subscriptionCustomDomainVerificationId);
        ArgumentNullException.ThrowIfNull(containerAppEnvironmentStaticIP);
        ArgumentNullException.ThrowIfNull(infrastructure);

        // Notice: `dnsZone` is assumed to be an existing resource not created under Aspire so we
        // cannot use AzureProvisioningResource.CreateExistingOrNewProvisionableResource here.

        // We are going to create a few DNS records to prove we own the domain
        // and then create the custom domain binding in the Container App.
        // We need access to a DnsZone resource to host those records.
        DnsZone dnsZone = DnsZone.FromExisting(nameof(dnsZone));
        dnsZone.Name = dnsDomain;
        infrastructure.Add(dnsZone);

        // TXT 'asuid' record is required and checked during containerApp deployment (due to configuration.ingress.customDomains)
        DnsTxtRecord dnsAsuidTxtRecord = new(nameof(dnsAsuidTxtRecord))
        {
            Name = $"asuid.{hostname}", // Remember: not arbitrary, must be 'asuid.<your-app-name>'
            TtlInSeconds = 3600,
            Parent = dnsZone,
            TxtRecords = {
                    new DnsTxtRecordInfo()
                    {
                        Values = [ subscriptionCustomDomainVerificationId ]
                    }
                }
        };
        infrastructure.Add(dnsAsuidTxtRecord);

        // A record pointing to the CAE environment - required by the certificate auto-binding logic during cert creation and binding
        DnsARecord dnsRecordA = new(nameof(dnsRecordA))
        {
            Name = hostname, // Remember: not arbitrary, must be '<your-app-name>'
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
        infrastructure.Add(dnsRecordA);

        // Add outputs 
        infrastructure.Add(new ProvisioningOutput("dnsZoneName", typeof(string)) { Value = dnsZone.Name });
        infrastructure.Add(new ProvisioningOutput("hostname", typeof(string)) { Value = dnsRecordA.Name });
        infrastructure.Add(new ProvisioningOutput("fqdn", typeof(string)) { Value = $"{hostname}.{dnsDomain}" });

        return (dnsAsuidTxtRecord, dnsRecordA);
    }
}