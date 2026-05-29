# Azure DNS Resolvability Timing Tests

This directory contains standalone tests that measure how long it takes for a DNS record created in Azure DNS to become resolvable by querying the authoritative nameservers directly.

## Why?

When deploying Azure Container Apps with custom domains and managed certificates, the deployment depends on DNS records (TXT and A) being resolvable at specific points during the process. Understanding the propagation delay between Azure DNS accepting a record creation and that record being queryable is critical to diagnosing deployment failures — particularly `InvalidCustomHostNameValidation` errors that can occur when the certificate binding happens before DNS has propagated.

These tests replicate the TXT record creation that `main.bicep` performs, but in isolation, so we can measure the DNS propagation delay without the complexity of a full deployment. By having multiple implementations (Bash/CLI, C#/ARM SDK, and the Bicep deployment itself), we can compare whether the API used to create the record affects propagation timing.

## Tests

| Target | Tool | Description |
|---|---|---|
| `make dns-test-bash` | Bash + Azure CLI + `dig` | Creates the TXT record via `az network dns record-set txt add-record`, then polls the authoritative NS with `dig` every 0.1s |
| `make dns-test-csharp` | C# + Azure SDK + DnsClient | Creates the TXT record via `Azure.ResourceManager.Dns`, then polls with the `DnsClient` NuGet package every 100ms |
| `make dns-test-cleanup` | Azure CLI | Deletes the TXT record created by either test |

## Configuration

All configuration is inherited from the root `Makefile` variables (`RESOURCE_GROUP_NAME`, `DNS_ZONE_NAME`, `CONTAINER_APP_NAME`). The TXT record value defaults to a timestamped dummy string but can be overridden:

```bash
make dns-test-bash DNS_TEST_TXT_VALUE=your-real-verification-id
```

## Prerequisites

- Azure CLI logged in (`az login`) with the correct subscription selected
- The DNS zone must already exist in the resource group
- .NET 10 SDK (for the C# test)
- `dig` (for the Bash test)
