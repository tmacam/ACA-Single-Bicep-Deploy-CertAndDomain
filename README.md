# ACA Single Bicep Deploy with Certificate and Domain

This repository provides a streamlined way to deploy Azure Container Apps (ACA) using a single Bicep template. Previously, it was a struggle to deploy managed certificates and custom domains, as multiple Bicep templates were required. You can find more context in this [GitHub issues thread](https://github.com/microsoft/azure-container-apps/issues/796).

Starting from the `2024-10-02-preview` API, we are supporting a bindingType `auto` which will automatically populate your domain information with any existing managed certificates. If no existing managed certificate is found, once the user provisions a new one, it will automatically bind to the hostname that the certificate was issued for. This feature is currently in `northcentralusstage` only. We will deploy it to all Azure Prod regions starting `Q1 2025`.

## Files

- **main.bicep**: The main Bicep template that defines the entire infrastructure for deploying ACA with a certificate and custom domain.
- **managedCertificates.bicep**: A Bicep template specifically for managing certificates within the deployment.

**Note**: This deployment uses the `2024-10-02-preview` API for Azure Container Apps. Please use with care as this feature is currently only in preview API version.

## Deployment Instructions

1. **Clone the repository**:
    ```sh
    git clone <repository-url>
    cd ACA-Single-Bicep-Deploy-CertAndDomain
    ```

2. **Deploy using Azure CLI**:
    ```sh
    az deployment group create --name <deployment-name> --resource-group <resource-group-name> --template-file main.bicep
    ```

## Benefits

- **Simplified Deployment**: You can now deploy your certs, domains, and apps all in one Bicep. This can be extended over to n apps, domains, and certs.

## Example Deployment

I ran this deployment template in my environment, and this was the custom domain used. You can browse it at:

http://single-bicep-custom-domain.tdaroly-dev.com/
