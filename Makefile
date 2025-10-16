RESOURCE_GROUP_NAME=acaSingleDeployCertAndDomain
LOCATION=eastus2
DNS_ZONE_NAME=apps.tmacam.dev
CONTAINER_APP_NAME=single-bicep-mcert-capp

PHONY: deploy create-rg create-dns-zone get-dns-zone-nameservers

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
deploy: create-rg
	@echo "🔎 Validating Azure ContainerApp deployment in resource group $(RESOURCE_GROUP_NAME)" && \
	az deployment group validate \
			--resource-group $(RESOURCE_GROUP_NAME) \
			--param dnsZoneName=$(DNS_ZONE_NAME) \
			--param containerAppName=$(CONTAINER_APP_NAME) \
			--template-file main.bicep -o table  && \
	echo "🚀 Deploying Azure ContainerApp in resource group $(RESOURCE_GROUP_NAME)" && \
	az deployment group create \
			--resource-group $(RESOURCE_GROUP_NAME) \
			--param dnsZoneName=$(DNS_ZONE_NAME) \
			--param containerAppName=$(CONTAINER_APP_NAME) \
			--template-file main.bicep --query "properties.outputs" && \
	echo "✅ Deployment finished"

