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
// TODO(tmacam): move this creation into ConfigureAutoBindingCustomDomain method?
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
