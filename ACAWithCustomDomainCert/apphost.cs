#:sdk Aspire.AppHost.Sdk@13.0.0-preview.1.25522.5
#:package Aspire.Hosting.Azure.AppContainers@13.0.0-preview.1.25522.5

#pragma warning disable ASPIRECOMPUTE001

using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Primitives;
using System.Net;


var builder = DistributedApplication.CreateBuilder(args);

string containerAppName = builder.AddParameter("containerAppName").ToString()
    ?? throw new MissingParameterValueException("containerAppName parameter is required.");
string dnsZoneName = builder.AddParameter("dnsZoneName").ToString()
    ?? throw new MissingParameterValueException("dnsZoneName parameter is required.");

// the container app name doesn't NEED to match the leaf part of the FQDN but let's keep it simple, shall we?
var customDomainFqdn = $"{containerAppName}.{dnsZoneName}";

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
        var dnsAsuidTxtRecord = new TXT("dnsAsuidTxtRecord")
        {
            Name = $"asuid.{containerAppName}", // Remember: not arbitrary, must be 'asuid.<your-app-name>'
            Ttl = 3600,
            Parent = dnsZone,
            TXTRecords =
            {
                new TxtRecord()
                {
                    Value = { subscriptionCustomDomainVerificationId }
                }
            }
        };
        infra.Add(dnsAsuidTxtRecord);

        // A record pointing to the CAE environment - required by the certificate auto-binding logic during cert creation and binding
        var dnsRecordA = new A("dnsRecordA")
        {
            Name = containerAppName, // Remember: not arbitrary, must be '<your-app-name>'
            Ttl = 3600,
            Parent = dnsZone,
            ARecords =
            {
                new ARecord()
                {
                    Ipv4Address = containerAppEnvironmentStaticIP
                }
            }
            //TargetResource = infra.GetResource<AzureContainerAppEnvironment>("cae")
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

#region DnsZone


// from https://github.com/eerhardt/CustomDomainTest/blob/main/CustomDomainTest.AppHost/DnsZones.cs

public partial class DnsZone : ProvisionableResource
{
    /// <summary>
    /// The name of the virtual network.
    /// </summary>
    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }
    private BicepValue<string>? _name;

    public DnsZone(string bicepIdentifier, string? resourceVersion = default)
        : base(bicepIdentifier, "Microsoft.Network/dnsZones", resourceVersion ?? "2023-07-01-preview")
    {
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>("Name", ["name"], isRequired: true);
    }

    public static DnsZone FromExisting(string bicepIdentifier, string? resourceVersion = default) =>
        new(bicepIdentifier, resourceVersion) { IsExistingResource = true };

    public override ResourceNameRequirements GetResourceNameRequirements() =>
        new(minLength: 2, maxLength: 64, validCharacters: ResourceNameCharacters.LowercaseLetters | ResourceNameCharacters.UppercaseLetters | ResourceNameCharacters.Numbers | ResourceNameCharacters.Hyphen | ResourceNameCharacters.Underscore | ResourceNameCharacters.Period);
}

// TXT and A record types should subclass from the same base class and only 
// differ in the constructor by the core azure resource type on the constructor

public partial class TXT : ProvisionableResource
{
    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }
    private BicepValue<string>? _name;

    public BicepValue<int> Ttl
    {
        get { Initialize(); return _ttl!; }
        set { Initialize(); _ttl!.Assign(value); }
    }
    private BicepValue<int>? _ttl;

    public DnsZone? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }
    private ResourceReference<DnsZone>? _parent;

    // not filtering for IpAddress.AddressFamily = AddressFamily.InterNetwork
    public BicepList<IPAddress> ARecords
    {
        get { Initialize(); return _aRecords!; }
        set { Initialize(); _aRecords!.Assign(value); }
    }
    private BicepList<IPAddress>? _aRecords;

    public BicepList<TxtRecord> TXTRecords
    {
        get { Initialize(); return _txtRecords!; }
        set { Initialize(); _txtRecords!.Assign(value); }
    }
    private BicepList<TxtRecord>? _txtRecords;

    public TXT(string bicepIdentifier, string? resourceVersion = default)
        : base(bicepIdentifier, "Microsoft.Network/dnsZones/TXT", resourceVersion ?? "2023-07-01-preview")
    {
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>("Name", ["name"], isRequired: true);
        _ttl = DefineProperty<int>("TTL", ["properties", "TTL"]);
        _parent = DefineResource<DnsZone>("Parent", ["parent"], isRequired: true);
        _aRecords = DefineListProperty<IPAddress>("ARecords", ["properties", "ARecords"]);
        _txtRecords = DefineListProperty<TxtRecord>("TXTRecords", ["properties", "TXTRecords"]);
    }

    public static TXT FromExisting(string bicepIdentifier, string? resourceVersion = default) =>
        new(bicepIdentifier, resourceVersion) { IsExistingResource = true };

    public override ResourceNameRequirements GetResourceNameRequirements() =>
        new(minLength: 2, maxLength: 64, validCharacters: ResourceNameCharacters.LowercaseLetters | ResourceNameCharacters.UppercaseLetters | ResourceNameCharacters.Numbers | ResourceNameCharacters.Hyphen | ResourceNameCharacters.Underscore | ResourceNameCharacters.Period);
}

public partial class A : ProvisionableResource
{
    /// <summary>
    /// The name of the virtual network.
    /// </summary>
    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }
    private BicepValue<string>? _name;

    public BicepValue<int> Ttl
    {
        get { Initialize(); return _ttl!; }
        set { Initialize(); _ttl!.Assign(value); }
    }
    private BicepValue<int>? _ttl;

    public DnsZone? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }
    private ResourceReference<DnsZone>? _parent;

    // not filtering for IpAddress.AddressFamily = AddressFamily.InterNetwork
    public BicepList<ARecord> ARecords
    {
        get { Initialize(); return _aRecords!; }
        set { Initialize(); _aRecords!.Assign(value); }
    }
    private BicepList<ARecord>? _aRecords;

    public BicepList<TxtRecord> TXTRecords
    {
        get { Initialize(); return _txtRecords!; }
        set { Initialize(); _txtRecords!.Assign(value); }
    }
    private BicepList<TxtRecord>? _txtRecords;

    public A(string bicepIdentifier, string? resourceVersion = default)
        : base(bicepIdentifier, "Microsoft.Network/dnsZones/A", resourceVersion ?? "2023-07-01-preview")
    {
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>("Name", ["name"], isRequired: true);
        _ttl = DefineProperty<int>("TTL", ["properties", "TTL"]);
        _parent = DefineResource<DnsZone>("Parent", ["parent"], isRequired: true);
        _aRecords = DefineListProperty<ARecord>("ARecords", ["properties", "ARecords"]);
        _txtRecords = DefineListProperty<TxtRecord>("TXTRecords", ["properties", "TXTRecords"]);
    }

    public static A FromExisting(string bicepIdentifier, string? resourceVersion = default) =>
        new(bicepIdentifier, resourceVersion) { IsExistingResource = true };

    public override ResourceNameRequirements GetResourceNameRequirements() =>
        new(minLength: 2, maxLength: 64, validCharacters: ResourceNameCharacters.LowercaseLetters | ResourceNameCharacters.UppercaseLetters | ResourceNameCharacters.Numbers | ResourceNameCharacters.Hyphen | ResourceNameCharacters.Underscore | ResourceNameCharacters.Period);
}
// https://learn.microsoft.com/en-us/azure/templates/microsoft.network/dnszones/a?pivots=deployment-language-bicep#arecord
public partial class ARecord : ProvisionableConstruct
{
    /// <summary>
    /// IPv4 address in the A record.
    /// </summary>
    public BicepValue<IPAddress> Ipv4Address
    {
        get { Initialize(); return _ipv4Address!; }
        set { Initialize(); _ipv4Address!.Assign(value); }
    }
    private BicepValue<IPAddress>? _ipv4Address;

    public ARecord()
    {
    }

    /// <summary>
    /// Define all the provisionable properties of ContainerAppCustomDomain.
    /// </summary>
    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _ipv4Address = DefineProperty<IPAddress>("Ipv4Address", ["ipv4Address"]);
    }
}

public partial class TxtRecord : ProvisionableConstruct
{
    /// <summary>
    /// Value of the TXT record. (Yes, the name is singular but it holds an array of strings. Awkward...)
    /// </summary>
    public BicepList<string> Value
    {
        get { Initialize(); return _value!; }
        set { Initialize(); _value!.Assign(value); }
    }
    private BicepList<string>? _value;

    public TxtRecord()
    {
    }

    /// <summary>
    /// Define all the provisionable properties of ContainerAppCustomDomain.
    /// </summary>
    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _value = DefineListProperty<string>("Value", ["value"]);
    }
}
#endregion