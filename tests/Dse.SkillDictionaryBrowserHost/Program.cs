using Dse.Domain;
using Dse.Infrastructure;
using Dse.Tests;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json.Serialization;

// Loopback-only test harness. No Azure, credentials, external AI, production auth or
// authority configuration is used. Its actors exist only in this memory store.
var builder=WebApplication.CreateBuilder(args);builder.WebHost.UseUrls("http://127.0.0.1:5182");
var fixture=await SkillDictionaryFixture.Create();
builder.Services.AddSingleton<IPrimaryRepository>(fixture.Repo);builder.Services.AddSingleton(fixture.Fence);
await fixture.Source("semantic-proposal","回帰試験の実施確認","私は回帰試験の実施確認を行った。");
await fixture.Source("semantic-failure","応答不正の試験","私は応答不正の試験を実施した。");
var wire=new SkillSemanticTestRuntime.Wire();var ai=SkillSemanticTestRuntime.Ai(fixture,SkillSemanticTestRuntime.Config(),wire);
builder.Services.AddSingleton(new SkillDictionaryService(fixture.Repo,fixture.Fence,new(fixture.Repo,TimeProvider.System),TimeProvider.System,()=>ai));
builder.Services.AddSingleton(new AuthorityProvider(fixture.Repo));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();builder.Services.AddSingleton<PageTokens>();
builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow);
var app=builder.Build();
app.Use(async(c,next)=>{
 c.Items[typeof(ActorAccess)]=c.Request.Headers["X-Test-Actor"].ToString() switch{"member"=>SkillDictionaryFixture.Member,"other"=>SkillDictionaryFixture.OtherScope,_=>SkillDictionaryFixture.Curator};
 try{await next();}catch(DomainException e){c.Response.StatusCode=e.Status;await c.Response.WriteAsJsonAsync(new{code=e.Code});}
 catch(ArgumentException){c.Response.StatusCode=400;await c.Response.WriteAsJsonAsync(new{code="INVALID_INPUT"});}
});
app.Lifetime.ApplicationStopped.Register(ai.Dispose);
app.MapGet("/api/test/semantic-audit",async()=>new{wire.Calls,runs=(await fixture.Repo.QueryAsync(RecordPartition.Catalog("demo"),"aiRun",null,null,100,null,default)).Items.Select(r=>new{id=r.Value.Text("id"),status=r.Value.Text("status")}).ToArray()});
app.MapGet("/health/live",()=>Results.Ok("test-only"));
app.MapGet("/api/config",()=>Results.Ok(new{mode="Demo",tenantId="demo",clientId="",scope="",dictionaryFixture=true}));
app.MapGroup("/api/v1").MapSkillDictionary();
app.Run();
