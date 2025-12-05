// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Dns;

namespace ACAWithCustomDomainCert;

#pragma warning disable AZPROVISION001 // DNS Provisioning is still in beta

// There isn't much sense in defining a custom resource for this.
// It is per se not harmful and there is potentially some use for it in
// *manual* DNS ownership verification scenarios.
//
// But in the context of Custom Domains with Auto-Binding Managed Certificates
// for container apps, its individual usage gets a bit lost.
// The custom domain is a configuration of a container app
// that requires what this AzureDnsOwnershipVerificationResource represents but not
// only that, it requires a manged certificate that is created as a child of the
// CAE hosting the container app, and that certificate too depends on the DNS records this
// resource represents. Requiring the developer to create a custom resource for this is a
// leaky abstraction.
//
// OTOH, having a custom resource to represent the whole "Custom Domains with
// Auto-Binding Managed Certificates" concept also doesn't seem appropriate either.
// It doesn't have an independent lifecycle, as it MUST be associated to an specific App.
//
// Keeping in line with other Configure... methods that take ContainerApps as a parameter,
// this method just goes ahead and creates all the resources to fullfil that configuration request.
// We are not requesting the developer to create Managed Identities used for the CAE, there is no point
// in exposing those required resources to the developer, so we are not doing that either.
[Obsolete("This class is deprecated and will be removed in a future release. Use AzureDnsOwnershipVerificationResourceExtension instead.")]
public class AzureDnsOwnershipVerificationResource(
    string name,
    string hostname,
    string dnsDomain,
    Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure)
{
    public string Hostname { get; } = hostname;
    public string DnsDomain { get; } = dnsDomain;
}

public static class AzureDnsOwnershipVerificationResourceExtension
{
    public static IResourceBuilder<AzureDnsOwnershipVerificationResource> AddAzureDnsOwnershipVerificationResource(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string hostname,
        string dnsDomain,
        IResourceBuilder<AzureContainerAppEnvironmentResource> cae)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));
        ArgumentException.ThrowIfNullOrEmpty(hostname, nameof(hostname));
        ArgumentException.ThrowIfNullOrEmpty(dnsDomain, nameof(dnsDomain));

        builder.AddAzureProvisioning();

        return builder.AddResource(new AzureDnsOwnershipVerificationResource(
            name,
            hostname,
            dnsDomain,
            infra =>
            {
                GetVerificationIdAndStaticIP(cae, infra, out var subscriptionCustomDomainVerificationId, out var containerAppEnvironmentStaticIP);
                ConfigureInfrastructure(
                    hostname: hostname,
                    dnsDomain: dnsDomain,
                    subscriptionCustomDomainVerificationId,
                    containerAppEnvironmentStaticIP,
                    infrastructure: infra);
            }
        ));
    }

    public static (DnsTxtRecord, DnsARecord) ConfigureInfrastructure(
        string hostname,
        string dnsDomain,
        BicepValue<string> subscriptionCustomDomainVerificationId,
        BicepValue<IPAddress> containerAppEnvironmentStaticIP,
        Infrastructure infrastructure)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostname, nameof(hostname));
        ArgumentException.ThrowIfNullOrEmpty(dnsDomain, nameof(dnsDomain));
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

    public static void GetVerificationIdAndStaticIP(
        IResourceBuilder<AzureContainerAppEnvironmentResource> cae,
        AzureResourceInfrastructure infrastructure,
        out BicepValue<string> subscriptionCustomDomainVerificationId,
        out BicepValue<IPAddress> containerAppEnvironmentStaticIP)
    {
        // CAE verificationId and IP Address
        //
        // Think of AzureResourceInfrastructure as a Bicep module. We are defining
        // a reference to an existing Container App Environment resource in this "scope"
        // so we have a resource we can refer to.
        ContainerAppManagedEnvironment containerAppEnvironment = (ContainerAppManagedEnvironment)cae.Resource.AddAsExistingResource(infrastructure);
        subscriptionCustomDomainVerificationId = containerAppEnvironment.CustomDomainConfiguration.CustomDomainVerificationId;
        containerAppEnvironmentStaticIP = containerAppEnvironment.StaticIP;
    }
}