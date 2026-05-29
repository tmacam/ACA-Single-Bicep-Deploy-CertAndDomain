#!/usr/bin/env dotnet

#:package Azure.ResourceManager.Dns@1.1.1
#:package Azure.Identity@1.13.2
#:package DnsClient@1.8.0
#:property PublishAot=false

using System.Diagnostics;
using System.Net;
using Azure;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;
using DnsClient;
using DnsClient.Protocol;

// ─── Configuration from environment variables ───
var resourceGroupName = RequireEnv("RESOURCE_GROUP_NAME");
var dnsZoneName = RequireEnv("DNS_ZONE_NAME");
var containerAppName = RequireEnv("CONTAINER_APP_NAME");

var txtValue = Environment.GetEnvironmentVariable("DNS_TEST_TXT_VALUE")
    ?? $"dummy-verification-id-{DateTime.UtcNow:yyyyMMddHHmmss}";
var recordName = $"asuid.{containerAppName}";
var fqdn = $"{recordName}.{dnsZoneName}";
int ttl = 15;
int timeoutSeconds = 300;
int pollIntervalMs = 100;

Console.WriteLine("=== DNS Resolvability Timing Test (C# / Azure SDK) ===");
Console.WriteLine($"  Record:  {fqdn} TXT");
Console.WriteLine($"  Value:   {txtValue}");
Console.WriteLine($"  TTL:     {ttl}s");
Console.WriteLine();

// ─── Step 1: Discover authoritative nameservers BEFORE any Azure calls ───
Console.WriteLine($"⏳ Discovering authoritative nameservers for {dnsZoneName}...");
var lookupClient = new LookupClient();
var nsResult = await lookupClient.QueryAsync(dnsZoneName, QueryType.NS);
var nsRecords = nsResult.Answers.NsRecords().ToList();

if (nsRecords.Count == 0)
{
    Console.Error.WriteLine($"❌ Could not discover nameservers for {dnsZoneName}. Is the zone properly delegated?");
    return 1;
}

Console.WriteLine("  Authoritative NS servers:");
foreach (var ns in nsRecords)
    Console.WriteLine($"    {ns.NSDName}");

// Resolve the first NS to an IP for direct queries
var firstNs = nsRecords[0].NSDName.Value;
var nsIps = await Dns.GetHostAddressesAsync(firstNs);
var nsIp = nsIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
    ?? nsIps.FirstOrDefault();
if (nsIp is null)
{
    Console.Error.WriteLine($"❌ Could not resolve nameserver {firstNs} to an IP address.");
    return 1;
}
var nsEndpoint = new IPEndPoint(nsIp, 53);
Console.WriteLine($"  Will poll: {firstNs} ({nsIp})");
Console.WriteLine();

// ─── Step 2: Create the TXT record via Azure Resource Manager SDK ───
Console.WriteLine($"⏳ Creating TXT record {recordName} in zone {dnsZoneName}...");

var totalStopwatch = Stopwatch.StartNew();

var credential = new DefaultAzureCredential();
var armClient = new ArmClient(credential);

var subscriptionId = Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID");
var subscription = string.IsNullOrEmpty(subscriptionId)
    ? await armClient.GetDefaultSubscriptionAsync()
    : (await armClient.GetSubscriptions().GetAsync(subscriptionId)).Value;
var resourceGroup = (await subscription.GetResourceGroups().GetAsync(resourceGroupName)).Value;
var dnsZone = (await resourceGroup.GetDnsZones().GetAsync(dnsZoneName)).Value;

var txtRecordCollection = dnsZone.GetDnsTxtRecords();
var txtRecordData = new DnsTxtRecordData
{
    TtlInSeconds = ttl,
};
txtRecordData.DnsTxtRecords.Add(new DnsTxtRecordInfo
{
    Values = { txtValue }
});

// CreateOrUpdate — this is the ARM call (analogous to az cli)
await txtRecordCollection.CreateOrUpdateAsync(WaitUntil.Started, recordName, txtRecordData);

Console.WriteLine("✅ ARM SDK returned. Starting resolution timer.");
Console.WriteLine();

// ─── Step 3: Poll authoritative NS until the record resolves ───
Console.WriteLine($"⏳ Polling {firstNs} for TXT record {fqdn} every {pollIntervalMs}ms (timeout: {timeoutSeconds}s)...");

var directClient = new LookupClient(new LookupClientOptions(nsEndpoint)
{
    UseCache = false,
});

var stopwatch = Stopwatch.StartNew();
int lastReportedSecond = -1;
int failedAttempts = 0;

while (stopwatch.Elapsed.TotalSeconds < timeoutSeconds)
{
    try
    {
        var queryResult = await directClient.QueryAsync(fqdn, QueryType.TXT);
        var txtRecords = queryResult.Answers.TxtRecords().ToList();

        foreach (var rec in txtRecords)
        {
            var joined = string.Join("", rec.Text);
            if (joined.Contains(txtValue, StringComparison.OrdinalIgnoreCase))
            {
                stopwatch.Stop();
                Console.WriteLine();
                Console.WriteLine("✅ TXT record resolved!");
                Console.WriteLine($"   Value:   \"{joined}\"");
                Console.WriteLine($"   Polling time until resolvable: {stopwatch.Elapsed.TotalSeconds:F3} seconds");
                Console.WriteLine($"   Total (including creation): {totalStopwatch.Elapsed.TotalSeconds:F3} seconds");
                Console.WriteLine($"   Failed attempts before success: {failedAttempts}");
                return 0;
            }
        }
    }
    catch
    {
        // Query may fail transiently — keep polling
    }

    failedAttempts++;

    int currentSecond = (int)stopwatch.Elapsed.TotalSeconds;
    if (currentSecond % 5 == 0 && currentSecond > 0 && currentSecond != lastReportedSecond)
    {
        lastReportedSecond = currentSecond;
        Console.Write($"\r  ... {stopwatch.Elapsed.TotalSeconds:F1}s elapsed");
    }

    await Task.Delay(pollIntervalMs);
}

Console.WriteLine();
Console.Error.WriteLine($"❌ Timeout after {timeoutSeconds}s — record did not resolve.");
Console.Error.WriteLine($"   Failed attempts: {failedAttempts}");
return 1;

// ─── Helpers ───

static string RequireEnv(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        Console.Error.WriteLine($"❌ Environment variable {name} is required but not set.");
        Environment.Exit(1);
    }
    return value;
}
