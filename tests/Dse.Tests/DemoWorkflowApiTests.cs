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

public sealed class DemoWorkflowApiTests
{
    private static WebApplicationFactory<Program> Factory()=>new WebApplicationFactory<Program>().WithWebHostBuilder(b=>
    {
        b.UseEnvironment("Development");b.UseSetting("Data:Mode","Demo");b.UseSetting("Ai:Mode","Mock");b.UseSetting("AiUsage:Stopped","false");
        b.UseSetting("AiUsage:MaxInputTokens","16000");b.UseSetting("AiUsage:MaxOutputTokens","8000");
        b.ConfigureServices(s=>s.AddSingleton<AuthorityProvider>(sp=>
        {
            var store=sp.GetRequiredService<IRecordStore>();DemoRecords.SeedAsync(store,"demo",default).GetAwaiter().GetResult();
            var p=RecordPartition.Catalog("demo");var row=store.ReadAsync(p,AuthorityProvider.PersonId(IdentityResolver.Demo.PersonId),default).GetAwaiter().GetResult()!;
            row.Value["roles"]=RecordJson.Array([..IdentityResolver.Demo.Roles,"operator"]);store.CommitAsync(p,[RecordWrite.Replace(row.Value,row.ETag)],default).GetAwaiter().GetResult();
            return new(sp.GetRequiredService<IPrimaryRepository>());
        }));
    });
    [Fact]
    public async Task Workflow_http_selection_is_exact_and_normal_routes_preserve_shared_records()
    {
        using var factory=Factory();using var client=factory.CreateClient();
        var response=await client.PostAsync("/api/v1/demo-workflows/start",null);response.EnsureSuccessStatusCode();var state=(await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var id=state.Text("id");Assert.StartsWith("wf-",id);client.DefaultRequestHeaders.Add("X-Dse-Workflow",id);
        var page=(await client.GetFromJsonAsync<JsonObject>("/api/v1/skill-candidates"))!;Assert.Equal(8,page["items"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/v1/organization" )).StatusCode);
        var original=await client.GetAsync("/api/v1/documents/doc-technical-list-a-distinct/original");original.EnsureSuccessStatusCode();Assert.NotEmpty(await original.Content.ReadAsByteArrayAsync());
        client.DefaultRequestHeaders.Remove("X-Dse-Workflow");
        Assert.Equal(0,(await client.GetFromJsonAsync<JsonObject>("/api/v1/skill-dictionary"))!.Number("version"));
        client.DefaultRequestHeaders.Add("X-Dse-Workflow",id+"-forged");Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/api/v1/skill-dictionary")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Dse-Workflow");client.DefaultRequestHeaders.Add("X-Dse-Workflow","");
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/api/v1/skill-dictionary")).StatusCode);
    }
}
