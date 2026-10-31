using Dse.Domain;
using Dse.Infrastructure;
using Dse.Tests;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json.Serialization;

// Loopback-only test harness. No Azure, credentials, external AI, production auth or
// authority configuration is used. Its actors exist only in this memory store.
var browserJson=new JsonSerializerOptions(JsonSerializerDefaults.Web);
var builder=WebApplication.CreateBuilder(args);builder.WebHost.UseUrls("http://127.0.0.1:5182");
var fixture=await SkillDictionaryFixture.Create();
builder.Services.AddSingleton<IPrimaryRepository>(fixture.Repo);builder.Services.AddSingleton(fixture.Fence);
await fixture.Source("semantic-proposal","回帰試験の実施確認","私は回帰試験の実施確認を行った。");
await fixture.Source("semantic-failure","応答不正の試験","私は応答不正の試験を実施した。");
await fixture.Source("semantic-oversize","装置の検証","私は装置の検証を実施した。"+new string('あ',3000)+"通常拠点を使用しない。本人の設計経験ではない。");
await fixture.Source("choice-outside","辞書外の調査","私は辞書外の調査を実施した。");
await fixture.Source("choice-compound","回帰試験と性能計測","私は回帰試験と性能計測を実施した。");
await fixture.Source("choice-legacy","過去方式の試験","私は過去方式の試験を実施した。");
await fixture.Source("recovery-stale","旧い候補","私は旧い候補を実施した。");
await fixture.Source("recovery-held","性能計測","私は性能計測を実施した。");
await fixture.Source("recovery-revoke","権限試験","私は権限試験を実施した。");
foreach(var id in new[]{"recovery-stale","recovery-revoke"})await fixture.Service.CollectAsync(SkillDictionaryFixture.Curator,Guid.NewGuid().ToString("N"),new("fragment-"+id),default);
async Task Change(string id,Action<JsonObject> change){var p=RecordPartition.Catalog("demo");var row=(await fixture.Repo.ReadAsync(p,id,default))!;var v=row.Value.Copy();change(v);await fixture.Store.CommitAsync(p,[RecordWrite.Replace(v,row.ETag)],default);}
await Change("fragment-recovery-stale",v=>{v["description"]="私は現在の候補を実施した。";v["specificSkills"]!["items"]![0]!["raw"]="現在の候補";v["specificSkills"]!["items"]![0]!["quote"]="私は現在の候補を実施した。";});
await Change("ev-recovery-stale",v=>v["text"]="私は現在の候補を実施した。");
await Change("fragment-recovery-held",v=>v["specificSkills"]!["items"]![0]!["quote"]="私は原文にない捏造技能を実施した。");
await fixture.Service.CollectAsync(SkillDictionaryFixture.Curator,Guid.NewGuid().ToString("N"),new("fragment-recovery-held"),default);
var wire=new SkillSemanticTestRuntime.Wire();var aiConfig=SkillSemanticTestRuntime.Config();aiConfig["AiUsage:MaxInputTokens"]="8000";
var ai=SkillSemanticTestRuntime.Ai(fixture,aiConfig,wire);
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
app.MapPost("/api/test/seed-noul",async(string candidateId)=>{
 var p=RecordPartition.Catalog("demo");var candidate=(await fixture.Repo.ReadAsync(p,candidateId,default))!;var version=(await fixture.Repo.ReadAsync(p,SkillDictionaryService.HeadId,default))!.Value.Number("version");var snapRow=(await fixture.Repo.ReadAsync(p,"skill-dictionary-version-"+version,default))!;var snapshot=snapRow.Value["snapshot"]!.Deserialize<SkillDictionarySnapshot>(browserJson)!;
 var hash=ConversationService.Hash(RecordJson.Canonical(JsonSerializer.SerializeToNode(snapshot,browserJson)!));var id="skill-proposal-"+ConversationService.Hash(SkillDictionaryFixture.Curator.PrincipalId+":"+candidateId+":"+candidate.Value.Text("sourceFingerprint")+":"+hash+":"+SkillSemanticMatching.Version);
 var element=candidate.Value["element"]!.Deserialize<SkillElement>(browserJson)!;var selected=SkillSemanticMatching.Select(SkillSemanticMatching.Applicable(snapshot,element,["demo"]),element);
 var row=RecordJson.New("skillMatchProposal",id,"demo",SkillDictionaryFixture.Curator.PrincipalId,["demo"],DateTimeOffset.UtcNow);row["candidateId"]=candidateId;row["sourceFingerprint"]=candidate.Value.Text("sourceFingerprint");row["dictionaryVersion"]=version;row["snapshotHash"]=hash;row["policyVersion"]=SkillSemanticMatching.Version;row["phase"]="complete";row["outcome"]="existing_candidate";row["reason"]="SEMANTIC_PROPOSAL_ONLY";row["basis"]="semantic_noul";row["targetIds"]=RecordJson.Array(selected.Select(c=>c.Id));row["applicableCount"]=selected.Length;row["shortlistComplete"]=true;row["comparisons"]=JsonSerializer.SerializeToNode(selected.Select(c=>new SkillEquivalence(c.Id,.91,.92,.91,true)),browserJson);row["providerExecutionUncertain"]=false;row["aiRunId"]=null;
 await fixture.Store.CommitAsync(p,[RecordWrite.Create(row)],default);return Results.Ok(new{proposalId=id});
});
// Test-only mutation, loopback memory fixture only. Production endpoints stay read/collect.
app.MapPost("/api/test/recovery-revoke",async()=>{await Change("doc-recovery-revoke",v=>v["activeVersion"]=null);return Results.Ok();});
app.MapPost("/api/test/recovery-change",async()=>{await Change("doc-recovery-stale",v=>v["accessVersion"]=v.Number("accessVersion")+1);return Results.Ok();});
app.MapGet("/health/live",()=>Results.Ok("test-only"));
app.MapGet("/api/config",()=>Results.Ok(new{mode="Demo",tenantId="demo",clientId="",scope="",dictionaryFixture=true}));
app.MapGroup("/api/v1").MapSkillDictionary();
app.Run();
