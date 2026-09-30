using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dse.Tests;

public class RecordApiTests
{
    private static WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Development");
        b.UseSetting("Data:Mode", "Demo");
    });
    private static HttpRequestMessage Patch(string id, string? etag, string key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/people/" + id) { Content = JsonContent.Create(body) };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }
    [Fact]
    public async Task Profile_update_replays_etag_rejects_stale_and_blocks_old_projection()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var before = await client.GetAsync("/api/v1/people/demo-person-01");
        var etag = before.Headers.ETag!.ToString();
        var body = new ProfileUpdate("架空訂正", false, "correct", []);
        var first = await client.SendAsync(Patch("demo-person-01", etag, "profile-key-0001", body));
        first.EnsureSuccessStatusCode();
        var replay = await client.SendAsync(Patch("demo-person-01", etag, "profile-key-0001", body));
        Assert.Equal(first.Headers.ETag, replay.Headers.ETag);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(Patch("demo-person-01", etag, "profile-key-0002", body))).StatusCode);
        var result = await (await client.PostAsJsonAsync("/api/search", new SearchRequest([], [], ["引き継ぎ"]))).Content.ReadFromJsonAsync<SearchResult>();
        Assert.DoesNotContain(result!.Candidates, x => x.EntityId == "demo-person-01");
    }
    [Fact]
    public async Task Pipeline_trace_is_owner_only_and_withdrawal_remains_readable_to_self()
    {
        using var factory = Factory().WithWebHostBuilder(b => { b.UseSetting("Ai:Mode", "Mock"); b.UseSetting("AiUsage:Stopped", "false"); b.UseSetting("AiUsage:MaxOutputTokens", "3000"); });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/documents/demo-person-02-document/pipeline")).StatusCode);
        var upload = await factory.Services.GetRequiredService<DocumentUploads>().UploadAsync(IdentityResolver.Demo, "API統合", "profile", "x.docx", OfficeExtractor.WordMime, OfficeFixtures.Package(), "api-profile-upload-01", default);
        var worker = factory.Services.GetRequiredService<IngestionProcessor>();
        await worker.RunOnceAsync(default); await worker.RunOnceAsync(default);
        var status = await client.GetFromJsonAsync<JsonObject>("/api/v1/documents/" + upload.Body!["documentId"]!.GetValue<string>() + "/pipeline");
        Assert.Equal("ready", status!["job"]!["status"]!.GetValue<string>()); Assert.NotEmpty(status["runs"]!.AsArray());
        Assert.Contains(status["runs"]!.AsArray(), r => r!["operation"]!.GetValue<string>() == "structure");
        var before = await client.GetAsync("/api/v1/people/demo-person-01");
        var response = await client.SendAsync(Patch("demo-person-01", before.Headers.ETag!.ToString(), "api-withdraw-key-01", new ProfileUpdate("非公開本人", true, "correct", [], "withdrawn")));
        response.EnsureSuccessStatusCode();
        var person = (await client.GetFromJsonAsync<JsonObject>("/api/v1/people/demo-person-01"))!;
        Assert.Equal("withdrawn", person.Text("publicationState"));
        Assert.False(AccessPolicy.CanRead(IdentityResolver.DemoPerson("demo-person-02")!, person));
    }
    [Theory]
    [InlineData("tenantId", "other")]
    [InlineData("scopeIds", "other")]
    [InlineData("ownerId", "other")]
    public async Task Untrusted_authorization_fields_are_rejected(string field, string value)
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var body = JsonSerializerNode(); body[field] = value;
        var result = await client.SendAsync(Patch("demo-person-01", "\"1\"", "profile-key-0001", body));
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("application/problem+json", result.Content.Headers.ContentType!.MediaType);
        Assert.Equal("INVALID_INPUT", (await result.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
    }
    private static JsonObject JsonSerializerNode() => new() { ["displayName"] = "架空", ["available"] = true, ["action"] = "correct", ["experienceEdits"] = new JsonArray() };
    [Fact]
    public async Task Missing_fields_headers_and_other_person_are_rejected()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        var body = JsonSerializerNode();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Patch("demo-person-01", null, "profile-key-0001", body))).StatusCode);
        body.Remove("available");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Patch("demo-person-01", "\"1\"", "profile-key-0001", body))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Patch("demo-person-02", "\"1\"", "profile-key-0001", JsonSerializerNode()))).StatusCode);
    }
    [Fact]
    public async Task Evidence_get_rechecks_latest_document_and_rejects_bad_cursor()
    {
        using var factory = Factory(); using var client = factory.CreateClient();
        const string path = "/api/v1/documents/demo-person-01-document/evidence";
        (await client.GetAsync(path)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + "?continuationToken=forged")).StatusCode);
        var repo = factory.Services.GetRequiredService<IPrimaryRepository>();
        var partition = RecordPartition.Catalog("demo");
        var document = (await repo.ReadAsync(partition, "demo-person-01-document", default))!;
        document.Value["activeVersion"] = null;
        await repo.CommitAsync(partition, [RecordWrite.Replace(document.Value, document.ETag)], default);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(path)).StatusCode);
    }
}
