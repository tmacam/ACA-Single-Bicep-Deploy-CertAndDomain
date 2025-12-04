// #:sdk Aspire.AppHost.Sdk@13.0.1
// #:package Aspire.Hosting.Azure.AppContainers@13.1.0-preview.1.25578.2
// #:package Azure.Provisioning.Dns@1.0.0-beta.1

#pragma warning disable ASPIRECOMPUTE001
#pragma warning disable AZPROVISION001

using ACAWithCustomDomainCert;

// TODOS:
// * Instead of AddAzureInfrastructure, model the custom domain as its own resource that implements AzureBicepResource.
// * Use Outputs and a ProvisioningOutputReference to resolve the CustomDomainVerificationid from the CAE resource instead of using an existing reference
// * When you use BicepOutputReference it implicitly creates the "dependson" relationship when the resources get provisioned.




var containerAppName = "myapp";
var dnsZoneName = "apps.tmacam.dev";
// the container app name doesn't NEED to match the leaf part of the FQDN but let's keep it simple, shall we?
var customDomainFqdn = $"{containerAppName}.{dnsZoneName}";

var builder = DistributedApplication.CreateBuilder(args);

var cae = builder.AddAzureContainerAppEnvironment("aspireContainerEnv");

var app = builder.AddContainer(containerAppName, "mcr.microsoft.com/k8se/quickstart:latest")
    .WithComputeEnvironment(cae)
    .WithContainerName(containerAppName)  // I really want a pretty name to refer to
    .WithHttpsEndpoint(targetPort: 80)
    .WithExternalHttpEndpoints()
    .PublishAsAzureContainerApp((infra, app) =>
    {
        app.ConfigureAutoBindingCustomDomain(cae, infra, containerAppName, dnsZoneName);
    });

// Enact the deployment
builder.Build().Run();
// .. and we are done!
