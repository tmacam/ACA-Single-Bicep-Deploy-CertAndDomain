RESOURCE_GROUP_NAME=acaSingleDeployCertAndDomain
LOCATION=eastus2

.PHONY: deploy nameServers


deploy: 
	@echo "📁 Creating resource group $(RESOURCE_GROUP_NAME) in location $(LOCATION)" && \
	az group create --name $(RESOURCE_GROUP_NAME) --location $(LOCATION) -o table && \
	echo "🔎 Validating Azure ContainerApp deployment in resource group $(RESOURCE_GROUP_NAME)" && \
	az deployment group validate \
					--resource-group $(RESOURCE_GROUP_NAME) \
					--template-file main.bicep -o table  && \
	echo "🚀 Deploying Azure ContainerApp in resource group $(RESOURCE_GROUP_NAME)" && \
	az deployment group create \
			--resource-group $(RESOURCE_GROUP_NAME) \
			--template-file main.bicep --query "properties.outputs" && \
	echo "✅ Deployment finished"

nameServers:
	az network dns zone show --name apps.tmacam.dev --resource-group $(RESOURCE_GROUP_NAME) --query nameServers