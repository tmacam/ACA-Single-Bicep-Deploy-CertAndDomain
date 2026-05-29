#!/usr/bin/env bash
set -euo pipefail

# DNS Resolvability Timing Test — Bash + Azure CLI version
#
# Creates a TXT DNS record via Azure CLI and measures how long it takes
# for the record to become resolvable by querying the authoritative nameservers directly.
#
# Required environment variables:
#   RESOURCE_GROUP_NAME - Azure resource group containing the DNS zone
#   DNS_ZONE_NAME       - The DNS zone name (e.g., apps.tmacam.dev)
#   CONTAINER_APP_NAME  - The container app name (used to form the record name: asuid.<name>)

: "${RESOURCE_GROUP_NAME:?RESOURCE_GROUP_NAME is required}"
: "${DNS_ZONE_NAME:?DNS_ZONE_NAME is required}"
: "${CONTAINER_APP_NAME:?CONTAINER_APP_NAME is required}"

RECORD_NAME="asuid.${CONTAINER_APP_NAME}"
FQDN="${RECORD_NAME}.${DNS_ZONE_NAME}"
TXT_VALUE="${DNS_TEST_TXT_VALUE:-dummy-verification-id-$(date +%Y%m%d%H%M%S)}"
TTL=15
POLL_INTERVAL=0.1
TIMEOUT=300

echo "=== DNS Resolvability Timing Test (Bash) ==="
echo "  Record:  ${FQDN} TXT"
echo "  Value:   ${TXT_VALUE}"
echo "  TTL:     ${TTL}s"
echo ""

# ─── Step 1: Discover authoritative nameservers BEFORE any Azure calls ───
echo "⏳ Discovering authoritative nameservers for ${DNS_ZONE_NAME}..."
NS_SERVERS=$(dig NS "${DNS_ZONE_NAME}" +short | head -4)
if [[ -z "${NS_SERVERS}" ]]; then
    echo "❌ Could not discover nameservers for ${DNS_ZONE_NAME}. Is the zone properly delegated?"
    exit 1
fi
# Pick the first nameserver for polling
NS_SERVER=$(echo "${NS_SERVERS}" | head -1)
echo "  Authoritative NS servers:"
echo "${NS_SERVERS}" | sed 's/^/    /'
echo "  Will poll: ${NS_SERVER}"
echo ""

# ─── Step 2: Create the TXT record via Azure CLI ───
echo "⏳ Creating TXT record ${RECORD_NAME} in zone ${DNS_ZONE_NAME}..."
TOTAL_START_TIME=$(date +%s%N)
az network dns record-set txt add-record \
    --resource-group "${RESOURCE_GROUP_NAME}" \
    --zone-name "${DNS_ZONE_NAME}" \
    --record-set-name "${RECORD_NAME}" \
    --value "${TXT_VALUE}" \
    -o none

# # Also set the TTL on the record set
# az network dns record-set txt update \
#     --resource-group "${RESOURCE_GROUP_NAME}" \
#     --zone-name "${DNS_ZONE_NAME}" \
#     --name "${RECORD_NAME}" \
#     --set "ttl=${TTL}" \
#     -o none

echo "✅ Azure CLI returned. Starting resolution timer."
echo ""

# ─── Step 3: Poll authoritative NS until the record resolves ───
echo "⏳ Polling ${NS_SERVER} for TXT record ${FQDN} every ${POLL_INTERVAL}s (timeout: ${TIMEOUT}s)..."

START_TIME=$(date +%s%N)
ELAPSED=0
FAILED_ATTEMPTS=0

while true; do
    # Query the authoritative nameserver directly
    RESULT=$(dig "@${NS_SERVER}" TXT "${FQDN}" +short 2>/dev/null || true)

    # Check if the expected value is in the response (dig returns quoted strings)
    if echo "${RESULT}" | grep -Fqi "${TXT_VALUE}"; then
        END_TIME=$(date +%s%N)
        ELAPSED_NS=$((END_TIME - START_TIME))
        ELAPSED_S=$(echo "scale=3; ${ELAPSED_NS} / 1000000000" | bc)
        echo ""
        echo "✅ TXT record resolved!"
        echo "   Value:   ${RESULT}"
        echo "   Polling time until resolvable: ${ELAPSED_S} seconds"
        TOTAL_ELAPSED_NS=$((END_TIME - TOTAL_START_TIME))
        TOTAL_ELAPSED_S=$(echo "scale=3; ${TOTAL_ELAPSED_NS} / 1000000000" | bc)
        echo "   Total (including creation): ${TOTAL_ELAPSED_S} seconds"
        echo "   Failed attempts before success: ${FAILED_ATTEMPTS}"
        exit 0
    fi

    FAILED_ATTEMPTS=$((FAILED_ATTEMPTS + 1))

    # Check timeout
    CURRENT=$(date +%s%N)
    ELAPSED_NS=$((CURRENT - START_TIME))
    ELAPSED_S=$(echo "scale=1; ${ELAPSED_NS} / 1000000000" | bc)

    # Compare as integers (seconds)
    ELAPSED_INT=${ELAPSED_S%.*}
    ELAPSED_INT=${ELAPSED_INT:-0}
    if (( ELAPSED_INT >= TIMEOUT )); then
        echo ""
        echo "❌ Timeout after ${TIMEOUT}s — record did not resolve."
        echo "   Failed attempts: ${FAILED_ATTEMPTS}"
        exit 1
    fi

    # Brief progress indicator every 5 seconds
    if (( ELAPSED_INT % 5 == 0 )) && (( ELAPSED_INT > 0 )); then
        printf "\r  ... %ss elapsed" "${ELAPSED_S}"
    fi

    sleep "${POLL_INTERVAL}"
done
