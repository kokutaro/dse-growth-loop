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
    private static WebApplicationFactory<Program> Factory()=>SyntheticTestApiTests.Factory().WithWebHostBuilder(b=>
    {
        b.UseSetting("Ai:Mode","Mock");b.UseSetting("AiUsage:Stopped","false");
        b.UseSetting("AiUsage:MaxInputTokens","16000");b.UseSetting("AiUsage:MaxOutputTokens","8000");
        b.ConfigureServices(s=>s.AddSingleton<IIngestionBlobs>(new WorkflowIngestionTests.Blobs()).AddSingleton<IAiTransport>(MockAiTransport.Instance));
    });
    [Fact]
    public async Task Workflow_http_selection_is_exact_and_normal_routes_preserve_shared_records()
    {
        using var factory=Factory();using var client=factory.CreateClient();
        client.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",SyntheticTestApiTests.Token());
        var response=await client.PostAsync("/api/v1/demo-workflows/start",null);response.EnsureSuccessStatusCode();var state=(await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var id=state.Text("id");Assert.StartsWith("wf-",id);client.DefaultRequestHeaders.Add("X-Dse-Workflow",id);
        var page=(await client.GetFromJsonAsync<JsonObject>("/api/v1/skill-candidates"))!;Assert.Equal(8,page["items"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/v1/organization" )).StatusCode);
        var original=await client.GetAsync("/api/v1/documents/doc-technical-list-a-distinct/original");original.EnsureSuccessStatusCode();Assert.NotEmpty(await original.Content.ReadAsByteArrayAsync());
        client.DefaultRequestHeaders.Remove("X-Dse-Workflow");
        Assert.Equal(0,(await client.GetFromJsonAsync<JsonObject>("/api/v1/skill-dictionary"))!.Number("version"));
        client.DefaultRequestHeaders.Add("X-Dse-Workflow",id+"-forged");Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync("/api/v1/skill-dictionary")).StatusCode);
        // HttpClient removes an empty header before transport. Exercise that server boundary directly.
        var context=new Microsoft.AspNetCore.Http.DefaultHttpContext();context.Request.Path="/api/v1/skill-dictionary";context.Request.Headers[DemoWorkflowEndpoints.Header]="";
        context.RequestServices=factory.Services;context.Items[typeof(ActorAccess)]=await factory.Services.GetRequiredService<AuthorityProvider>().PersonAsync("11111111-1111-4111-8111-111111111111","fixture-manager-person",default);
        var next=false;Assert.Equal(404,(await Assert.ThrowsAsync<DomainException>(()=>DemoWorkflowEndpoints.SelectAsync(context,()=>{next=true;return Task.CompletedTask;}))).Status);Assert.False(next);
    }
    [Fact]
    public async Task Normal_http_upload_correction_and_withdrawal_run_in_the_selected_workflow()
    {
        using var factory=Factory();using var client=factory.CreateClient();client.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",SyntheticTestApiTests.Token());
        var state=await (await client.PostAsync("/api/v1/demo-workflows/start",null)).Content.ReadFromJsonAsync<JsonObject>();client.DefaultRequestHeaders.Add(DemoWorkflowEndpoints.Header,state!.Text("id"));
        async Task<HttpResponseMessage> File(string path,bool revision=false,string text="C#で回帰試験を実施した。",string? etag=null)
        {
            using var form=new MultipartFormDataContent();var content=new ByteArrayContent(OfficeFixtures.Package(body:$"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>"));content.Headers.ContentType=new(OfficeExtractor.WordMime);form.Add(content,"file","fictional.docx");
            if(revision)form.Add(new StringContent("架空の訂正"),"reason");else{form.Add(new StringContent("架空のHTTP資料"),"title");form.Add(new StringContent("profile"),"documentType");}
            using var request=new HttpRequestMessage(HttpMethod.Post,path){Content=form};request.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));if(etag is not null)request.Headers.TryAddWithoutValidation("If-Match",etag);return await client.SendAsync(request);
        }
        var accepted=await File("/api/v1/documents");accepted.EnsureSuccessStatusCode();var body=(await accepted.Content.ReadFromJsonAsync<JsonObject>())!;var id=body.Text("documentId");
        var worker=factory.Services.GetRequiredService<IngestionProcessor>();await worker.RunOnceAsync(default);await worker.RunOnceAsync(default);
        var doc=await client.GetAsync("/api/v1/documents/"+id);doc.EnsureSuccessStatusCode();var record=(await doc.Content.ReadFromJsonAsync<JsonObject>())!;Assert.Equal("complete",record.Text("aiState"));
        var revised=await File("/api/v1/documents/"+id+"/versions",true,"C#で回帰試験を実施した。架空の訂正版。",doc.Headers.ETag!.ToString());revised.EnsureSuccessStatusCode();
        Assert.Empty((await (await client.PostAsJsonAsync("/api/search",new SearchRequest([],[],["回帰試験"]))).Content.ReadFromJsonAsync<SearchResult>())!.Candidates);
        await worker.RunOnceAsync(default);await worker.RunOnceAsync(default);doc=await client.GetAsync("/api/v1/documents/"+id);record=(await doc.Content.ReadFromJsonAsync<JsonObject>())!;Assert.Equal(2,record.Number("activeVersion"));
        using var withdraw=new HttpRequestMessage(HttpMethod.Post,"/api/v1/documents/"+id+"/withdraw"){Content=JsonContent.Create(new{reason="架空資料の撤回"})};withdraw.Headers.Add("Idempotency-Key",Guid.NewGuid().ToString("N"));withdraw.Headers.TryAddWithoutValidation("If-Match",doc.Headers.ETag!.ToString());
        (await client.SendAsync(withdraw)).EnsureSuccessStatusCode();Assert.Equal(HttpStatusCode.Conflict,(await client.GetAsync("/api/v1/documents/"+id+"/evidence")).StatusCode);
    }
}
