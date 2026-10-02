using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// Explicit, bounded validation only. No DefaultAzureCredential/operator fallback or inference.
public static class ConnectionSmoke
{
    public static bool Denied(int status) => status == 403;

    public static void Validate(IConfiguration config)
    {
        static bool Endpoint(string? value, string suffix) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && uri.Host.EndsWith(suffix, StringComparison.Ordinal) && uri.AbsolutePath == "/" &&
            uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "" && uri.Port == 443;
        if (config["Data:Mode"] != "Azure" || config["ConnectionSmoke:Approved"] != "true" || config["ConnectionSmoke:DummyOnly"] != "true" ||
            string.IsNullOrWhiteSpace(config["ConnectionSmoke:ApprovalReference"]) ||
            !Regex.IsMatch(config["ConnectionSmoke:RunId"] ?? "", @"\Aissue9-[a-z0-9-]{1,40}\z") ||
            config["ConnectionSmoke:Role"] is not ("api" or "worker") ||
            !Guid.TryParse(config["AZURE_CLIENT_ID"], out _) || !Guid.TryParse(config["Auth:TenantId"], out _) ||
            config["Cosmos:Database"] != "dse-growth-loop" ||
            !Endpoint(config["Cosmos:Endpoint"], ".documents.azure.com") ||
            !Endpoint(config["Storage:BlobEndpoint"], ".blob.core.windows.net") ||
            !Endpoint(config["Storage:QueueEndpoint"], ".queue.core.windows.net") || !Endpoint(config["KeyVault:Uri"], ".vault.azure.net") ||
            !Regex.IsMatch(config["Jev:SecretVersion"] ?? "", @"\A[0-9a-f]{32}\z"))
            throw new InvalidOperationException("Connection smoke requires explicit Azure dummy-only approval, isolated run ID, fixed secret version and MI");
    }

    public static async Task<bool> RunAsync(IConfiguration config, CancellationToken ct)
    {
        Validate(config);
        var role = config["ConnectionSmoke:Role"]!;
        var id = config["ConnectionSmoke:RunId"]!;
        var tenant = config["Auth:TenantId"]!;
        var credential = new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(config["AZURE_CLIENT_ID"]!));
        using var cosmos = new CosmosClient(config["Cosmos:Endpoint"], credential, new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
        var blobOptions = new Azure.Storage.Blobs.BlobClientOptions(); blobOptions.Retry.MaxRetries = 0;
        var queueOptions = new QueueClientOptions(); queueOptions.Retry.MaxRetries = 0;
        var blobs = new BlobServiceClient(new Uri(config["Storage:BlobEndpoint"]!), credential, blobOptions);
        var queues = new QueueServiceClient(new Uri(config["Storage:QueueEndpoint"]!), credential, queueOptions);
        var originals = blobs.GetBlobContainerClient("originals").GetBlobClient("validation/" + id + ".txt");
        var extracted = blobs.GetBlobContainerClient("extracted").GetBlobClient("validation/" + id + ".txt");
        var ingestion = queues.GetQueueClient("ingestion");
        var results = new List<object>();
        var passed = true;
        async Task Check(string name, Func<Task<double?>> action, bool expectDenied = false)
        {
            var start = Stopwatch.GetTimestamp();
            int? status = null; double? ru = null; var success = false;
            try { ru = await action(); success = !expectDenied; }
            catch (RequestFailedException e) { status = e.Status; success = expectDenied && Denied(e.Status); }
            catch (CosmosException e) { status = (int)e.StatusCode; success = expectDenied && Denied((int)e.StatusCode); }
            catch (HttpRequestException e) { status = e.StatusCode is null ? null : (int)e.StatusCode; success = expectDenied && status == 403; }
            catch (Exception) when (!ct.IsCancellationRequested) { /* No exception/endpoint/secret body is exported. */ }
            passed &= success;
            results.Add(new { name, expected = expectDenied ? "denied-403" : "allowed", status = success ? "passed" : "failed", httpStatus = status, requestCharge = ru, elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds });
        }
        foreach (var containerName in new[] { "catalog", "conversations" })
        {
            var container = cosmos.GetContainer("dse-growth-loop", containerName);
            var recordId = id + "-" + role + "-" + containerName;
            var partition = containerName == "catalog" ? tenant : recordId;
            await Check(containerName + "-create", async () =>
            {
                var response = await container.CreateItemAsync(new { id = recordId, tenantId = tenant, sessionId = recordId, kind = "validation", dummyOnly = true, approvalReference = config["ConnectionSmoke:ApprovalReference"] }, new PartitionKey(partition), cancellationToken: ct);
                return response.RequestCharge;
            });
            await Check(containerName + "-read", async () =>
            {
                using var response = await container.ReadItemStreamAsync(recordId, new PartitionKey(partition), cancellationToken: ct);
                response.EnsureSuccessStatusCode();
                using var document = await JsonDocument.ParseAsync(response.Content, cancellationToken: ct);
                if (document.RootElement.GetProperty("id").GetString() != recordId || !document.RootElement.GetProperty("dummyOnly").GetBoolean()) throw new InvalidOperationException();
                return response.Headers.RequestCharge;
            });
        }
        if (role == "api")
        {
            await Check("originals-upload", async () => { await originals.UploadAsync(BinaryData.FromString("DSE fictional validation " + id), overwrite: false, cancellationToken: ct); return null; });
            await Check("originals-read", async () => { var r = await originals.DownloadContentAsync(ct); if (r.Value.Content.ToString() != "DSE fictional validation " + id) throw new InvalidOperationException(); return null; });
            await Check("ingestion-send", async () => { await ingestion.SendMessageAsync(id, cancellationToken: ct); return null; });
            await Check("extracted-read-denied", async () => { await extracted.DownloadContentAsync(ct); return null; }, true);
            await Check("ingestion-receive-denied", async () => { await ingestion.ReceiveMessagesAsync(1, TimeSpan.FromSeconds(30), ct); return null; }, true);
        }
        else
        {
            await Check("originals-read", async () => { var r = await originals.DownloadContentAsync(ct); if (r.Value.Content.ToString() != "DSE fictional validation " + id) throw new InvalidOperationException(); return null; });
            await Check("extracted-upload", async () => { await extracted.UploadAsync(BinaryData.FromString("DSE fictional extracted " + id), overwrite: false, cancellationToken: ct); return null; });
            await Check("extracted-read", async () => { var r = await extracted.DownloadContentAsync(ct); if (r.Value.Content.ToString() != "DSE fictional extracted " + id) throw new InvalidOperationException(); return null; });
            await Check("ingestion-receive-extend-delete", async () =>
            {
                var r = await ingestion.ReceiveMessagesAsync(1, TimeSpan.FromSeconds(30), ct);
                var message = r.Value.SingleOrDefault();
                if (message?.MessageText != id) throw new InvalidOperationException("Isolated queue required");
                var updated = await ingestion.UpdateMessageAsync(message.MessageId, message.PopReceipt, message.MessageText, TimeSpan.FromSeconds(60), ct);
                await ingestion.DeleteMessageAsync(message.MessageId, updated.Value.PopReceipt, ct); return null;
            });
            await Check("poison-send", async () => { await queues.GetQueueClient("ingestion-poison").SendMessageAsync(id, cancellationToken: ct); return null; });
            await Check("originals-upload-denied", async () => { await blobs.GetBlobContainerClient("originals").GetBlobClient("validation/" + id + "-denied.txt").UploadAsync(BinaryData.FromString("DSE dummy"), overwrite: false, cancellationToken: ct); return null; }, true);
        }
        await Check("key-vault-fixed-version-read", async () =>
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(["https://vault.azure.net/.default"]), ct);
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, config["KeyVault:Uri"]!.TrimEnd('/') + "/secrets/jev-api-key/" + config["Jev:SecretVersion"] + "?api-version=7.4");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await client.SendAsync(request, ct); response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (string.IsNullOrWhiteSpace(document.RootElement.GetProperty("value").GetString())) throw new InvalidOperationException(); return null;
        });
        Console.WriteLine(JsonSerializer.Serialize(new { runId = id, role, inferenceAttempts = 0, results }));
        return passed;
    }
}
