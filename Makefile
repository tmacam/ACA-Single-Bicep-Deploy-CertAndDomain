RESOURCE_GROUP_NAME=acaSingleDeployCertAndDomain
LOCATION=eastus2
DNS_ZONE_NAME=apps.tmacam.dev
CONTAINER_APP_NAME=single-bicep-geterr
MANAGED_ENVIRONMENT_NAME="env-${LOCATION}"
# MANAGED_ENVIRONMENT_NAME=aspirecontainerenv76lcyi

all: deploy

.PHONY: all deploy create-rg create-dns-zone get-dns-zone-nameservers dns-test-bash dns-test-csharp dns-test-cleanup

info:
	@echo "ℹ️ Deployment Information ℹ️"
	@echo "   📁 Resource Group: $(RESOURCE_GROUP_NAME)"
	@echo "   🌐 DNS Zone: $(DNS_ZONE_NAME)"
	@echo "   📍 Location: $(LOCATION)"
	@echo "   🚀 Container App Name: $(CONTAINER_APP_NAME)"
	@echo "   🏗️ Managed Environment Name: $(MANAGED_ENVIRONMENT_NAME)"

# Create the resource group to hold everything
# This is idempotent, so if the RG already exists it will just return it.
create-rg:
	@echo "📁 Creating resource group $(RESOURCE_GROUP_NAME) in location $(LOCATION)" && \
	az group create --name $(RESOURCE_GROUP_NAME) --location $(LOCATION) -o table

# This is the only step we assume the developer has done before running the deploy target
# This is because you need to have the DNS zone created before the deployment
# so the bicep can reference it as an existing resource.
# If the DNS zone already exists, this will just return it.
create-dns-zone: create-rg
	@echo "🌐 Creating DNS zone $(DNS_ZONE_NAME) in resource group $(RESOURCE_GROUP_NAME)" && \
	az network dns zone create --name $(DNS_ZONE_NAME) --resource-group $(RESOURCE_GROUP_NAME) \
		--query "nameServers" -o table

# You need this information to configure your DNS zone in your domain registrar.
# The list of which Azure NS to add as authoritative name servers is not predictable and matters.
get-dns-zone-nameservers:
	az network dns zone show --name $(DNS_ZONE_NAME) --resource-group $(RESOURCE_GROUP_NAME) --query nameServers


# Deploy the Container App with custom domain and managed certificate as a single bicep deployment
# This target depends on the create-rg target to ensure the RG exists before deploying
# It does NOT depend on create-dns-zone because we assume the DNS zone is pre-existing
deploy: info
	@echo "🔎 Validating Azure ContainerApp deployment in resource group $(RESOURCE_GROUP_NAME)" && \
	az deployment group validate \
			--resource-group $(RESOURCE_GROUP_NAME) \
			--param dnsZoneName=$(DNS_ZONE_NAME) \
			--param location=$(LOCATION) \
			--param containerAppName=$(CONTAINER_APP_NAME) \
			--template-file main.bicep -o table  && \
	echo "🚀 Deploying Azure ContainerApp in resource group $(RESOURCE_GROUP_NAME)" && \
	az deployment group create \
			--name "single-bicep-deploy-$(LOCATION)-$(shell date +%Y%m%d%H%M%S)" \
			--resource-group $(RESOURCE_GROUP_NAME) \
			--param dnsZoneName=$(DNS_ZONE_NAME) \
			--param location=$(LOCATION) \
			--param containerAppName=$(CONTAINER_APP_NAME) \
			--template-file main.bicep --query "properties.outputs" && \
	echo "✅ Deployment finished"


# ─── DNS Resolvability Timing Tests ───

DNS_TEST_RECORD_NAME=asuid.$(CONTAINER_APP_NAME)
DNS_TEST_TXT_VALUE?=dummy-verification-id-$(shell date +%Y%m%d%H%M%S)

# Run the Bash DNS resolvability test
dns-test-bash: info
	@echo "🧪 Running DNS resolvability test (Bash)..." && \
	RESOURCE_GROUP_NAME=$(RESOURCE_GROUP_NAME) \
	DNS_ZONE_NAME=$(DNS_ZONE_NAME) \
	CONTAINER_APP_NAME=$(CONTAINER_APP_NAME) \
	DNS_TEST_TXT_VALUE=$(DNS_TEST_TXT_VALUE) \
	./azureDNSTests/dns-test.sh

# Run the C# DNS resolvability test (requires .NET 10 SDK)
dns-test-csharp: info
	@echo "🧪 Running DNS resolvability test (C#)..." && \
	AZURE_SUBSCRIPTION_ID=$$(az account show --query id -o tsv) \
	RESOURCE_GROUP_NAME=$(RESOURCE_GROUP_NAME) \
	DNS_ZONE_NAME=$(DNS_ZONE_NAME) \
	CONTAINER_APP_NAME=$(CONTAINER_APP_NAME) \
	DNS_TEST_TXT_VALUE=$(DNS_TEST_TXT_VALUE) \
	dotnet run azureDNSTests/DnsTest.cs

# Remove the TXT DNS record created by the DNS tests
dns-test-cleanup:
	@echo "🧹 Removing TXT record $(DNS_TEST_RECORD_NAME) from zone $(DNS_ZONE_NAME)..." && \
	az network dns record-set txt delete \
		--resource-group $(RESOURCE_GROUP_NAME) \
		--zone-name $(DNS_ZONE_NAME) \
		--name $(DNS_TEST_RECORD_NAME) \
		--yes -o none && \
	echo "✅ DNS test record removed"

