using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dse.Tests;

public class ApiTests
{
    private const string Tenant = "11111111-1111-4111-8111-111111111111";
    private const string Client = "22222222-2222-4222-8222-222222222222";
    private const string User = "33333333-3333-4333-8333-333333333333";
    // テスト専用の署名素材。Entra に使用しない。
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-only-signing-material-0123456789abcdef"));
    private static SearchRequest Request => new([new("domain", "software")], [], []);

    private static WebApplicationFactory<Program> Factory(bool demo) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment(demo ? "Development" : "Production");
        foreach (var entry in new Dictionary<string, string>
        {
            ["Data:Mode"] = demo ? "Demo" : "Azure", ["Cosmos:Endpoint"] = "https://unit-test.documents.azure.com/",
            ["Auth:TenantId"] = Tenant, ["Auth:ClientId"] = Client, ["Auth:AllowedObjectIds:0"] = User,
            ["Auth:Bindings:0:TenantId"] = Tenant, ["Auth:Bindings:0:Oid"] = User,
            ["Auth:Bindings:0:PrincipalId"] = "principal-test", ["Auth:Bindings:0:PersonId"] = "demo-person-01",
            ["Auth:Bindings:0:Roles:0"] = "member", ["Auth:Bindings:0:Enabled"] = "true",
            ["Auth:Grants:0:PrincipalId"] = "principal-test", ["Auth:Grants:0:ScopeIds:0"] = "demo"
        }) b.UseSetting(entry.Key, entry.Value);
        b.ConfigureServices(s =>
        {
            if (!demo) s.AddSingleton<ICatalog, DemoCatalog>();
            if (!demo)
            {
                s.AddSingleton<IRecordStore,MemoryRecordStore>();
                s.AddSingleton<AuthorityProvider>(sp =>
                {
                    var cfg = sp.GetRequiredService<IConfiguration>(); var store = sp.GetRequiredService<IRecordStore>();
                    AuthorityFixture.InitializeAsync(store,cfg,Tenant).GetAwaiter().GetResult();
                    return new(sp.GetRequiredService<IPrimaryRepository>(),cfg);
                });
            }
            s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                o.Configuration = new OpenIdConnectConfiguration { Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0" };
                o.Configuration.SigningKeys.Add(Key);
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(o.Configuration);
            });
        });
    });

    private static string Token(string tenant = Tenant, string user = User, string scope = "access_as_user", string audience = Client) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken($"https://login.microsoftonline.com/{Tenant}/v2.0", audience,
            [new Claim("tid", tenant), new Claim("oid", user), new Claim("scp", scope)],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));

    [Fact]
    public async Task Development_search_returns_secondary_category_and_sources()
    {
        using var factory = Factory(true);
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/search", Request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SearchResult>();
        Assert.Equal("demo-person-03", Assert.Single(result!.Candidates).EntityId);
        Assert.NotEmpty(result.Candidates[0].Units[0].SourceRefs);
        Assert.Equal("not_configured", result.RerankingStatus);
    }

    [Theory]
    [InlineData(true, "/api/v2/search")]
    [InlineData(true, "/api/v2/search/conditions")]
    [InlineData(false, "/api/v2/search")]
    [InlineData(false, "/api/v2/search/conditions")]
    public async Task Invalid_live_configuration_is_reported_before_real_AI_dependency_resolution(bool demo, string path)
    {
        // Keep the real AI registrations. Missing Demo persistence/Azure MI used to throw
        // during DI before the configuration response could be returned. No AI transport fake.
        using var factory = Factory(demo).WithWebHostBuilder(b =>
        {
            b.UseSetting("Ai:Mode", "Live"); b.UseSetting("AiUsage:Stopped", "true");
            // This validation-only legacy route requires a dummy operator as well as member.
            if (!demo) b.UseSetting("Auth:Bindings:0:Roles:1", "operator");
        });
        using var client = factory.CreateClient();
        if (!demo) client.DefaultRequestHeaders.Authorization = new("Bearer", Token());
        using var response = await client.PostAsJsonAsync(path, new { query = "引き継ぎを改善したい" });
        if (path.EndsWith("/conditions"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var conversion = (await response.Content.ReadFromJsonAsync<QueryConversion>())!;
            Assert.Equal("manual_required:configuration", conversion.Status); Assert.Null(conversion.Conditions);
        }
        else
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Contains("AI_SEARCH_CONFIGURATION_INVALID", await response.Content.ReadAsStringAsync());
        }
        var repo = factory.Services.GetRequiredService<IPrimaryRepository>();
        var partition = RecordPartition.Catalog(demo ? "demo" : Tenant);
        Assert.Empty((await repo.QueryAsync(partition, "aiRun", null, null, 100, null, default)).Items);
        var fence = await repo.ReadAsync(partition, "system-access-fence", default);
        Assert.True(string.IsNullOrEmpty(fence?.Value.Text("holder")));
    }

    [Fact]
    public async Task Invalid_input_unknown_route_and_rate_limit_are_distinct()
    {
        using var factory = Factory(true);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/search", Request with { Limit = 101 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/no-such-endpoint")).StatusCode);
        for (var i = 0; i < 29; i++) (await client.PostAsJsonAsync("/api/search", Request)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/search", Request)).StatusCode);
    }

    [Fact]
    public async Task Production_requires_a_valid_access_token()
    {
        using var factory = Factory(false);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/search", Request)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/search", Request)).StatusCode);
        using var operatorFactory = Factory(false).WithWebHostBuilder(b => b.UseSetting("Auth:Bindings:0:Roles:1", "operator"));
        using var operatorClient = operatorFactory.CreateClient();
        operatorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        (await operatorClient.PostAsJsonAsync("/api/search", Request)).EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(audience: "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/search", Request)).StatusCode);
    }

    [Theory]
    [InlineData("other", User, "access_as_user")]
    [InlineData(Tenant, "other", "access_as_user")]
    [InlineData(Tenant, User, "other")]
    public async Task Production_rejects_wrong_tenant_user_or_scope(string tenant, string user, string scope)
    {
        using var factory = Factory(false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(tenant, user, scope));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/search", Request)).StatusCode);
    }

    [Fact]
    public void Production_cannot_register_demo_storage()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Data:Mode"] = "Demo" }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCatalog(config, false));
    }

    [Fact]
    public async Task Health_and_config_do_not_expose_secrets()
    {
        using var factory = Factory(true);
        using var client = factory.CreateClient();
        (await client.GetAsync("/health/live")).EnsureSuccessStatusCode();
        (await client.GetAsync("/health/ready")).EnsureSuccessStatusCode();
        var config = await client.GetStringAsync("/api/config");
        Assert.Contains("Demo", config);
        Assert.DoesNotContain("Cosmos", config);
    }
    [Fact]
    public async Task Disabled_binding_cannot_use_an_otherwise_valid_JWT()
    {
        using var factory = Factory(false).WithWebHostBuilder(b => b.UseSetting("Auth:Bindings:0:Enabled", "false"));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/search", Request)).StatusCode);
    }

    [Fact]
    public async Task Operator_role_does_not_imply_member_search_permission()
    {
        using var factory = Factory(false).WithWebHostBuilder(b => b.UseSetting("Auth:Bindings:0:Roles:0", "operator"));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/search", Request)).StatusCode);
    }
    [Theory]
    [InlineData("/api/v1/documents", "POST")]
    [InlineData("/api/v2/search/conditions", "POST")]
    [InlineData("/api/v2/search", "POST")]
    [InlineData("/api/v2/retrieval-runs/unknown", "GET")]
    [InlineData("/api/v1/documents/unknown/original", "GET")]
    [InlineData("/api/v1/jobs/unknown/retry", "POST")]
    public async Task Ingestion_routes_require_authentication(string path, string method)
    {
        using var factory = Factory(false); using var client = factory.CreateClient();
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    [Theory]
    [InlineData("/api/hubs/conversations/negotiate?negotiateVersion=1", "POST")]
    [InlineData("/api/v1/challenges", "GET")]
    [InlineData("/api/v1/sessions/unknown/messages", "POST")]
    public async Task Conversation_routes_never_accept_demo_headers_in_production(string path, string method)
    {
        using var factory = Factory(false); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-Person", "demo-person-02");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/conversation-context")).StatusCode);
    }
    [Theory]
    [InlineData("/api/v1/challenges", "GET")]
    [InlineData("/API/V1/Challenges/", "GET")]
    [InlineData("/API/SEARCH/", "POST")]
    [InlineData("/api/v1/SessionS/opaque/messages/", "GET")]
    [InlineData("/api/v1/conversation-context/", "GET")]
    [InlineData("/api/v1/conversation-context", "GET")]
    [InlineData("/api/v1/sessions/opaque/messages", "GET")]
    [InlineData("/api/v1/sessions/opaque/knowledge", "GET")]
    [InlineData("/api/v2/search", "POST")]
    [InlineData("/api/v2/retrieval-runs/opaque", "GET")]
    [InlineData("/api/hubs/conversations/negotiate?negotiateVersion=1", "POST")]
    public async Task Production_employee_cannot_reidentify_collaborators_through_legacy_routes(string path, string method)
    {
        using var factory = Factory(false); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("principal-test", body);
        Assert.DoesNotContain("demo-person-01", body);
    }

    [Fact]
    public async Task Production_self_profile_endpoint_does_not_become_an_employee_directory()
    {
        var store = new MemoryRecordStore();
        foreach (var pair in new[] { ("demo-person-01", "principal-test"), ("other-person", "other-principal") })
        {
            var person = RecordJson.New("person", pair.Item1, Tenant, pair.Item2, ["demo"], DateTimeOffset.UtcNow);
            person["displayName"] = pair.Item1 == "other-person" ? "private-other-name" : "self-name";
            await store.CommitAsync(RecordPartition.Catalog(Tenant), [RecordWrite.Create(person)], default);
        }
        using var factory = Factory(false).WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordStore>(store)));
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/people/demo-person-01")).StatusCode);
        var other = await client.GetAsync("/api/v1/people/other-person");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        var missing = await client.GetAsync("/api/v1/people/unknown-person");
        Assert.Equal(other.StatusCode,missing.StatusCode);
        Assert.Equal((await other.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!["code"]!.GetValue<string>(),
            (await missing.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!["code"]!.GetValue<string>());
        Assert.DoesNotContain("private-other-name", await other.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_raw_jobs_are_self_only_with_an_explicit_operator_exception(bool isOperator)
    {
        var store = new MemoryRecordStore();
        foreach (var (id, owner) in new[] { ("self-job", "principal-test"), ("other-job", "other-principal") })
        {
            var job = RecordJson.New("job", id, Tenant, owner, ["demo"], DateTimeOffset.UtcNow);
            job["documentId"] = id + "-private-document";
            job["aiRun"] = new System.Text.Json.Nodes.JsonObject { ["providerRequestId"] = id + "-provider-request" };
            await store.CommitAsync(RecordPartition.Catalog(Tenant), [RecordWrite.Create(job)], default);
        }
        using var factory = Factory(false).WithWebHostBuilder(b =>
        {
            if (isOperator) b.UseSetting("Auth:Bindings:0:Roles:1", "operator");
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(store));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        var own = await client.GetAsync("/api/v1/jobs/self-job");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.NotNull(own.Headers.ETag);
        var other = await client.GetAsync("/api/v1/jobs/other-job");
        if (isOperator)
        {
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
            Assert.Contains("other-job-provider-request", await other.Content.ReadAsStringAsync());
        }
        else
        {
            var missing = await client.GetAsync("/api/v1/jobs/missing-job");
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
            Assert.Equal(missing.StatusCode, other.StatusCode);
            var missingBody = (await missing.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!;
            var otherBody = (await other.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!;
            foreach (var field in new[] { "code", "status", "title", "detail", "type" })
                Assert.Equal(missingBody[field]?.ToJsonString(), otherBody[field]?.ToJsonString());
            Assert.Null(other.Headers.ETag);
            foreach (var privateValue in new[] { "other-principal", "other-job-private-document", "other-job-provider-request", "aiRun" })
                Assert.DoesNotContain(privateValue, await other.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("principal-test")]
    [InlineData("other-principal")]
    public async Task Production_employee_cannot_read_raw_conversation_jobs_even_as_a_participant(string owner)
    {
        const string sid = "private-session";
        var store = new MemoryRecordStore();
        var session = RecordJson.New("session", sid, Tenant, owner, ["demo"], DateTimeOffset.UtcNow);
        session["sessionId"] = sid;
        session["participantIds"] = RecordJson.Array(["demo-person-01", "other-person"]);
        var job = RecordJson.New("job", "conversation-job", Tenant, owner, ["demo"], DateTimeOffset.UtcNow);
        job["container"] = "conversations";
        job["sessionId"] = sid;
        job["participantIds"] = session["participantIds"]!.DeepClone();
        await store.CommitAsync(RecordPartition.Conversation(Tenant, sid), [RecordWrite.Create(session), RecordWrite.Create(job)], default);
        using var factory = Factory(false).WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordStore>(store)));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        var result = await client.GetAsync("/api/v1/jobs/conversation-job?sessionId=" + sid);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Null(result.Headers.ETag);
        foreach (var privateValue in new[] { owner, "other-person", sid, "participantIds" })
            Assert.DoesNotContain(privateValue, await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Hub_rejects_expired_tokens_and_disabled_bindings()
    {
        using var factory = Factory(false); using var client = factory.CreateClient();
        var expired = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken($"https://login.microsoftonline.com/{Tenant}/v2.0", Client,
            [new Claim("tid", Tenant), new Claim("oid", User), new Claim("scp", "access_as_user")],
            DateTime.UtcNow.AddMinutes(-20), DateTime.UtcNow.AddMinutes(-10), new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expired);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/hubs/conversations/negotiate?negotiateVersion=1", null)).StatusCode);
        using var disabled = Factory(false).WithWebHostBuilder(b => b.UseSetting("Auth:Bindings:0:Enabled", "false"));
        using var disabledClient = disabled.CreateClient();
        disabledClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        Assert.Equal(HttpStatusCode.Forbidden, (await disabledClient.PostAsync("/api/hubs/conversations/negotiate?negotiateVersion=1", null)).StatusCode);
    }

}
