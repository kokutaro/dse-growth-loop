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
            s.AddSingleton<ICatalog, DemoCatalog>();
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
        (await client.PostAsJsonAsync("/api/search", Request)).EnsureSuccessStatusCode();
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
}
