#:sdk Aspire.AppHost.Sdk@13.0.0-preview.1.25522.5
#:package Aspire.Hosting.Azure.AppContainers@13.0.0-preview.1.25522.5

#pragma warning disable ASPIRECOMPUTE001

using System.ComponentModel;
using System.Data.Common;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppContainers;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Client;

var builder = DistributedApplication.CreateBuilder(args);


//var containerAppName = builder.AddParameter("containerAppName");
// var customDomainFqdn = builder.AddParameter("customDomainFqdn");
var containerAppName = "mycontainerapp";
var customDomainFqdn = "mycontainerapp.apps.tmacam.dev";

// We can use appsettings.json configuration for Azure provisioning or hardcode it here.
//
// builder.Services.Configure<AzureProvisioningOptions>(options =>
// {
//     options.SubscriptionId = "<sub-id>";
//     options.ResourceGroup = "rg-aspire-demo"; // exact group
//     options.Location = "eastus";
//     options.AllowResourceGroupCreation = false;
// });

var cae = builder.AddAzureContainerAppEnvironment("aspireContainerEnv");

// var customDomainConfiguration = cae.Resource.AddAsExistingResource();

var app = builder.AddContainer(containerAppName, "mcr.microsoft.com/k8se/quickstart:latest")
    .WithComputeEnvironment(cae)
    .WithContainerName(containerAppName)  // I really want a pretty name to refer to
    .WithHttpsEndpoint(targetPort: 80)
    .WithExternalHttpEndpoints()
    .PublishAsAzureContainerApp((infra, app) =>
    {
        app.ConfigureAutoBindingCustomDomain(customDomainFqdn);
    });



builder.Build().Run();


#region  my extensions
public static class CustomDnsContainerAppExtensions {
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

        app.ResourceVersion = "2024-10-02-preview"; // BindingType:auto is only available for now in preview API versions from 2024-10 onwwards
    }
}
#endregion