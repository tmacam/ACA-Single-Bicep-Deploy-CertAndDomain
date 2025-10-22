#:sdk Aspire.AppHost.Sdk@13.0.0-preview.1.25522.5
#:package Aspire.Hosting.Azure.AppContainers@13.0.0-preview.1.25522.5

#pragma warning disable ASPIRECOMPUTE001

using Aspire.Hosting.Azure;
using Microsoft.Extensions.DependencyInjection;

var containerAppName = "mycontainerapp";

var builder = DistributedApplication.CreateBuilder(args);

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

var app = builder.AddContainer(containerAppName, "mcr.microsoft.com/k8se/quickstart:latest")
    .WithComputeEnvironment(cae)
    .WithContainerName(containerAppName)  // I really want a pretty name to refer to
    .WithHttpsEndpoint(targetPort: 80)
    .WithExternalHttpEndpoints();

builder.Build().Run();
