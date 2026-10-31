using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Dse.Tests;
public sealed class SyntheticTestApiTests
{
    private const string Tenant="11111111-1111-4111-8111-111111111111",Client="22222222-2222-4222-8222-222222222222",Oid="33333333-3333-4333-8333-333333333333",MemberOid="44444444-4444-4444-8444-444444444444";
    // Local JWT fixture only, never accepted by Entra or any deployed service.
    private static readonly SymmetricSecurityKey Key=new(Encoding.UTF8.GetBytes("synthetic-test-only-signing-material-0123456789abcdef"));
    private static WebApplicationFactory<Program> Factory()
    {
        var store=new MemoryRecordStore();var bindings=new[]{new IdentityBinding(Tenant,Oid,"fixture-manager","fixture-manager-person",["member","operator"],true),new IdentityBinding(Tenant,MemberOid,"fixture-member","fixture-member-person",["member"],true)};
        var grants=new[]{new AccessGrant("fixture-manager",["fixture"]),new AccessGrant("fixture-member",["fixture"])};
        AuthorityProvider.InitializeAsync(store,Tenant,bindings,grants,default).GetAwaiter().GetResult();
        var manager=RecordJson.New("person","fixture-manager-person",Tenant,"fixture-manager",["fixture"],DateTimeOffset.UtcNow);manager["displayName"]="運用者fixture";manager["departmentId"]="existing-department";
        store.CommitAsync(RecordPartition.Catalog(Tenant),[RecordWrite.Create(manager)],default).GetAwaiter().GetResult();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b=>{
            b.UseEnvironment("Production");b.UseSetting("Data:Mode","Azure");b.UseSetting("Cosmos:Endpoint","https://unit-test.documents.azure.com/");
            b.UseSetting("Auth:TenantId",Tenant);b.UseSetting("Auth:ClientId",Client);b.UseSetting("Auth:AllowedObjectIds:0",Oid);b.UseSetting("Auth:AllowedObjectIds:1",MemberOid);
            for(var i=0;i<bindings.Length;i++)
            {
                var x=bindings[i];var prefix="Auth:Bindings:"+i+":";b.UseSetting(prefix+"TenantId",Tenant);b.UseSetting(prefix+"Oid",x.Oid);b.UseSetting(prefix+"PrincipalId",x.PrincipalId);b.UseSetting(prefix+"PersonId",x.PersonId);b.UseSetting(prefix+"Enabled","true");for(var j=0;j<x.Roles.Length;j++)b.UseSetting(prefix+"Roles:"+j,x.Roles[j]);
                b.UseSetting("Auth:Grants:"+i+":PrincipalId",x.PrincipalId);b.UseSetting("Auth:Grants:"+i+":ScopeIds:0","fixture");
            }
            b.ConfigureServices(s=>{
                s.AddSingleton<IRecordStore>(store);s.AddSingleton<IProjectionRepository,MemoryProjectionRepository>();
                var files=new LocalIngestionFiles(Path.Combine(Path.GetTempPath(),"synthetic-api-"+Guid.NewGuid().ToString("N")));
                s.AddSingleton<IIngestionQueue>(new LocalIngestionQueue(files,TimeProvider.System));
                s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,o=>{o.Configuration=new OpenIdConnectConfiguration{Issuer=$"https://login.microsoftonline.com/{Tenant}/v2.0"};o.Configuration.SigningKeys.Add(Key);o.ConfigurationManager=new StaticConfigurationManager<OpenIdConnectConfiguration>(o.Configuration);});
            });
        });
    }
    private static string Token(string oid=Oid)=>new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken($"https://login.microsoftonline.com/{Tenant}/v2.0",Client,[new Claim("tid",Tenant),new Claim("oid",oid),new Claim("scp","access_as_user")],DateTime.UtcNow.AddMinutes(-1),DateTime.UtcNow.AddMinutes(5),new SigningCredentials(Key,SecurityAlgorithms.HmacSha256)));
    private static HttpRequestMessage Registration(object input)=>new(HttpMethod.Post,"/api/v1/admin/test-people"){Content=JsonContent.Create(input),Headers={{"Idempotency-Key",Guid.NewGuid().ToString("N")}}};
    [Fact]
    public async Task Unauthenticated_and_nonoperator_cannot_register_list_or_search()
    {
        using var factory=Factory();using var client=factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1/admin/test-people")).StatusCode);
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",Token(MemberOid));
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/v1/admin/test-people")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(Registration(new{reference="a",displayName="架空A",scopeId="fixture",syntheticDeclaration=true}))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsJsonAsync("/api/v1/admin/test-people/search",new SearchRequest([],[],["test"]))).StatusCode);
    }
    [Fact]
    public async Task Real_operator_route_rejects_other_scope_and_untrusted_fields_and_preserves_self_boundary()
    {
        using var factory=Factory();using var client=factory.CreateClient();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",Token());
        Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(Registration(new{reference="bad",displayName="架空",scopeId="other",syntheticDeclaration=true}))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.SendAsync(Registration(new{reference="bad",displayName="架空",scopeId="fixture",syntheticDeclaration=true,personId="real-person"}))).StatusCode);
        var r=await client.SendAsync(Registration(new{reference="a",displayName="架空A",scopeId="fixture",syntheticDeclaration=true}));Assert.Equal(HttpStatusCode.Created,r.StatusCode);
        var person=(await r.Content.ReadFromJsonAsync<JsonObject>())!;Assert.True(person["syntheticTest"]!.GetValue<bool>());
        using var patch=new HttpRequestMessage(HttpMethod.Patch,"/api/v1/people/"+person.Text("id")){Content=JsonContent.Create(new ProfileUpdate("実社員になりすまし",true,"confirm",[]))};patch.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));patch.Headers.TryAddWithoutValidation("If-Match",r.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.Forbidden,(await client.SendAsync(patch)).StatusCode);
        var list=(await client.GetFromJsonAsync<JsonObject>("/api/v1/admin/test-people"))!;Assert.Single(list["items"]!.AsArray());
    }
}
