using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

public interface IAiCredentials
{
    Task<string> BearerAsync(string provider, CancellationToken ct);
}

public sealed class AzureAiCredentials(HttpClient http, TokenCredential credential, IConfiguration config) : IAiCredentials
{
    public async Task<string> BearerAsync(string provider, CancellationToken ct)
    {
        if (provider == "Foundry")
            return (await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), ct)).Token;
        if (!Uri.TryCreate(config["KeyVault:Uri"], UriKind.Absolute, out var vault) || vault.Scheme != "https" ||
            !vault.Host.EndsWith(".vault.azure.net", StringComparison.Ordinal) || !vault.IsDefaultPort || vault.AbsolutePath != "/" ||
            vault.UserInfo != "" || vault.Query != "" || vault.Fragment != "") throw new AiException(AiFailure.Unauthorized);
        var secret = config["Jev:SecretName"] ?? "jev-api-key";
        var version = config["Jev:SecretVersion"];
        if (!System.Text.RegularExpressions.Regex.IsMatch(secret, "^[A-Za-z0-9-]{1,127}$") ||
            string.IsNullOrWhiteSpace(version) || !System.Text.RegularExpressions.Regex.IsMatch(version, "^[a-fA-F0-9]{32}$"))
            throw new AiException(AiFailure.Unauthorized);
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://vault.azure.net/.default"]), ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(vault, $"secrets/{secret}/{version}?api-version=2025-07-01"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new AiException(AiFailure.Unauthorized);
        var body = await AiHttpTransport.ReadBoundedAsync(response, 32768, ct);
        try
        {
            var key = JsonNode.Parse(body)?["value"]?.GetValue<string>();
            return !string.IsNullOrWhiteSpace(key) ? key : throw new AiException(AiFailure.Unauthorized);
        }
        catch (JsonException) { throw new AiException(AiFailure.Unauthorized); }
    }
}

public sealed record AiWireResponse(JsonObject Body, AiUsage? Usage, string? RequestId = null);
public interface IAiTransport
{
    Task<AiWireResponse> SendAsync(AiModel model, JsonObject body, int maxResponseBytes, CancellationToken ct);
}

// CAS closes the cancellation/dispatch race: abandoned credential work can never send later.
public sealed class AiDispatchState
{
    private int phase; // 0 preparing, 1 dispatched, 2 abandoned before dispatch
    public bool TryAbandonBeforeDispatch() => Interlocked.CompareExchange(ref phase, 2, 0) == 0 || Volatile.Read(ref phase) == 2;
    public void MarkDispatched(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref phase, 1, 0) != 0) throw new OperationCanceledException(ct);
    }
}
public interface IDispatchTrackingAiTransport : IAiTransport
{
    Task<AiWireResponse> SendAsync(AiModel model, JsonObject body, int maxResponseBytes, CancellationToken ct, AiDispatchState dispatch);
}

// Raw HTTP has no SDK retry policy. The executor is the only inference retry owner.
public sealed class AiHttpTransport(HttpClient http, IAiCredentials credentials, TimeProvider clock, IConfiguration? config = null) : IDispatchTrackingAiTransport, IDisposable
{
    public void Dispose() => http.Dispose();
    public Task<AiWireResponse> SendAsync(AiModel model, JsonObject body, int maxResponseBytes, CancellationToken ct) =>
        SendAsync(model, body, maxResponseBytes, ct, new AiDispatchState());
    public async Task<AiWireResponse> SendAsync(AiModel model, JsonObject body, int maxResponseBytes, CancellationToken ct, AiDispatchState dispatch)
    {
        var endpoint = model.Provider == "Jev" ? model.Endpoint : new Uri(model.Endpoint, "openai/v1/chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        try { request.Headers.Authorization = new("Bearer", await credentials.BearerAsync(model.Provider, ct)); }
        catch (Exception e) when (e is Azure.Identity.AuthenticationFailedException or Azure.Identity.CredentialUnavailableException or Azure.RequestFailedException)
        { throw new AiException(AiFailure.Unauthorized) { InferenceNotDispatched = true }; }
        request.Content = new StringContent(AiCodecs.SerializeRequest(body), Encoding.UTF8, "application/json");
        if (config is not null && (AiLimits.Read(config).Stopped || !AiLimits.Read(config).LiveOptIn || config["Ai:Mode"] != "Live"))
            throw new AiException(AiFailure.Stopped);
        dispatch.MarkDispatched(ct);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var requestId = new[] { "x-request-id", "apim-request-id", "x-ms-request-id" }
            .SelectMany(name => response.Headers.TryGetValues(name, out var values) ? values : [])
            .FirstOrDefault(RecordJson.IsId);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow());
            if (retry < TimeSpan.Zero) retry = TimeSpan.Zero;
            throw new AiException(status is 401 or 403 ? AiFailure.Unauthorized : status == 429 ? AiFailure.RateLimited : AiFailure.Unavailable,
                status == 429 || status >= 500, retry, requestId) { ProviderRejected = status is >= 400 and < 500 and not 408 };
        }
        var bytes = await ReadBoundedAsync(response, maxResponseBytes, ct);
        try
        {
            var parsed = JsonNode.Parse(bytes)?.AsObject() ?? throw new JsonException();
            AiUsage? usage = null;
            if (parsed["usage"] is JsonObject u)
            {
                var input = u[model.Provider == "Jev" ? "input_tokens" : "prompt_tokens"]?.GetValue<long>();
                var output = u[model.Provider == "Jev" ? "output_tokens" : "completion_tokens"]?.GetValue<long>();
                if (input is not >= 0 || output is not >= 0) throw new AiException(AiFailure.SchemaMismatch, requestId: requestId);
                usage = new(input.Value, output.Value, false);
            }
            else if (parsed["usage"] is not null) throw new AiException(AiFailure.SchemaMismatch, requestId: requestId);
            return new(parsed, usage, requestId);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw new AiException(AiFailure.InvalidJson); }
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > max) throw new AiException(AiFailure.OutputLimit);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var result = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (result.Length + read > max) throw new AiException(AiFailure.OutputLimit);
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
}
