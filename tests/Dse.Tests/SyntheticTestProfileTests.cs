using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dse.Tests;

public sealed class SyntheticHarness : IDisposable
{
    public static readonly ActorAccess Operator=new("demo","test-manager","test-manager-person",["member","operator"],["demo"]);
    public static readonly ActorAccess Member=new("demo","test-member","test-member-person",["member"],["demo"]);
    public static readonly ActorAccess Other=new("demo","other-manager","other-manager-person",["member","operator"],["other"]);
    public IRecordStore Store {get;}=new MemoryRecordStore();
    public IPrimaryRepository Repo {get;}
    public AccessFence Fence {get;}
    public IConfiguration Config {get;}=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Data:Mode"]="Demo",["Ai:Mode"]="Mock"}).Build();
    public SyntheticTestProfiles Service {get;}
    public DocumentUploads Uploads {get;}
    public IngestionProcessor Processor {get;}
    public ProfilePipeline Pipeline {get;}
    public FaultQueue Queue {get;}
    private readonly string directory=Path.Combine(Path.GetTempPath(),"dse-synthetic-"+Guid.NewGuid().ToString("N"));
    public SyntheticHarness()
    {
        Repo=new PrimaryRepository(Store);Fence=new(Repo);
        var actors=new[]{Operator,Member,Other};
        AuthorityProvider.InitializeAsync(Store,"demo",actors.Select(a=>new IdentityBinding("demo","",a.PrincipalId,a.PersonId,a.Roles,true)).ToArray(),actors.Select(a=>new AccessGrant(a.PrincipalId,a.ScopeIds)).ToArray(),default).GetAwaiter().GetResult();
        var manager=RecordJson.New("person",Operator.PersonId,"demo",Operator.PrincipalId,["demo"],DateTimeOffset.UtcNow);
        manager["departmentId"]="existing-department";manager["displayName"]="運用者fixture";
        Store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Create(manager)],default).GetAwaiter().GetResult();
        var clock=TimeProvider.System;var idem=new IdempotencyExecutor(Repo,clock);Service=new(Repo,Fence,idem,Config,clock);
        var files=new LocalIngestionFiles(directory);var queue=Queue=new FaultQueue(new LocalIngestionQueue(files,clock));var blobs=new LocalIngestionBlobs(files);var options=new IngestionOptions();var extractor=new OfficeExtractor(options);var notices=new JobNotifications(Repo,queue,clock);
        var ai=new PipelineAi();Pipeline=new(Repo,Store,Fence,ai,ai,new(Config),Config,clock,new SyntheticFixtureSkills());
        Uploads=new(Repo,Fence,idem,blobs,extractor,options,notices,clock,Config);
        Processor=new(Repo,blobs,queue,extractor,options,Fence,clock,NullLogger<IngestionProcessor>.Instance,Pipeline,notices);
    }
    public async Task<JsonObject> Person(string reference="a") => (await Service.RegisterAsync(Operator,new(reference,"架空担当 "+reference,"demo",true),Guid.NewGuid().ToString("N"),default)).Body!.AsObject();
    public async Task<MutationResponse> Upload(JsonObject person,byte[]? bytes=null,string? key=null) => await Uploads.UploadTestAsync(Operator,person.Text("id"),"synthetic.xlsx",OfficeExtractor.ExcelMime,bytes??InternalResumeFixture.Package(InternalResumeFixture.Cells(count:2)),key??Guid.NewGuid().ToString("N"),default);
    public async Task<JsonObject> Read(string id)=>(await Repo.ReadAsync(RecordPartition.Catalog("demo"),id,default))!.Value;
    public async Task Revoke(bool scopes=false)
    {
        var row=(await Repo.ReadAsync(RecordPartition.Catalog("demo"),AuthorityProvider.PersonId(Operator.PersonId),default))!;
        row.Value[scopes?"scopeIds":"roles"]=RecordJson.Array(scopes?["other"]:["member"]);
        await Store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(row.Value,row.ETag)],default);
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
// Deterministic transport double for flow/quote validation; not an AI quality result.
public sealed class SyntheticFixtureSkills : IConcreteSkillExtractor
{
    public Task<AiResult<SpecificSkills>> ExtractSkillsAsync(AiContext c,string text,CancellationToken ct)
    {
        var raw=text.Contains("回帰試験",StringComparison.Ordinal)?"回帰試験":text.Split('\n',StringSplitOptions.RemoveEmptyEntries)[0];
        return Task.FromResult(new AiResult<SpecificSkills>(AiStatus.Succeeded,new(SpecificSkillRules.Version,"extracted",[new(raw,null,c.Evidence[0].Id,raw){Basis="experience"}]),"mock-synthetic-skills",null));
    }
}
public sealed class SyntheticTestProfileTests
{
    [Fact]
    public async Task Registration_is_data_only_distinct_idempotent_and_bounded()
    {
        using var h=new SyntheticHarness();var first=await h.Person();var repeat=await h.Person();
        Assert.Equal(first.Text("id"),repeat.Text("id"));Assert.StartsWith(SyntheticTestAccess.Label,first.Text("displayName"));
        var b=await h.Person("b");var c=await h.Person("c");Assert.Equal(3,new[]{first,b,c}.Select(x=>x.Text("id")).Distinct().Count());
        Assert.Null(await new AuthorityProvider(h.Repo).PersonAsync("demo",first.Text("id"),default));
        Assert.Equal(3,(await h.Repo.QueryAsync(RecordPartition.Catalog("demo"),"personAuthority",null,null,100,null,default)).Items.Length);
        Assert.Equal("TEST_PERSON_LIMIT",(await Assert.ThrowsAsync<DomainException>(()=>h.Person("d"))).Code);
        Assert.Equal("TEST_REFERENCE_CONFLICT",(await Assert.ThrowsAsync<DomainException>(()=>h.Service.RegisterAsync(SyntheticHarness.Operator,new("a","別名","demo",true),Guid.NewGuid().ToString("N"),default))).Code);
    }
    [Fact]
    public async Task Concurrent_and_lost_response_registration_never_duplicates()
    {
        using var h=new SyntheticHarness();var input=new SyntheticTestRegistration("a","架空A","demo",true);var key=Guid.NewGuid().ToString("N");
        var results=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>h.Service.RegisterAsync(SyntheticHarness.Operator,input,key,default)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(results[0].Body!.ToJsonString()),JsonNode.Parse(results[1].Body!.ToJsonString())));
        Assert.Single((await h.Service.ListUnderFenceAsync(SyntheticHarness.Operator,default))["items"]!.AsArray());
    }
    [Theory]
    [InlineData("member")][InlineData("scope")][InlineData("tenant")]
    public async Task Nonoperator_other_scope_and_tenant_cannot_register_or_upload(string mode)
    {
        using var h=new SyntheticHarness();var p=await h.Person();var actor=mode=="member"?SyntheticHarness.Member:mode=="scope"?SyntheticHarness.Other:SyntheticHarness.Operator with{TenantId="other"};
        await Assert.ThrowsAsync<DomainException>(()=>h.Service.RegisterAsync(actor,new("bad","架空","demo",true),Guid.NewGuid().ToString("N"),default));
        await Assert.ThrowsAsync<DomainException>(()=>h.Uploads.UploadTestAsync(actor,p.Text("id"),"a.xlsx",OfficeExtractor.ExcelMime,InternalResumeFixture.Package(),Guid.NewGuid().ToString("N"),default));
    }
    [Fact]
    public async Task Same_scope_other_operator_does_not_own_profile_and_self_boundary_stays_closed()
    {
        using var h=new SyntheticHarness();var p=await h.Person();
        Assert.False(AccessPolicy.CanRead(SyntheticHarness.Member,p));
        Assert.False(AccessPolicy.CanRead(SyntheticHarness.Operator with{PersonId="other",PrincipalId="other"},p));
        Assert.Throws<DomainException>(()=>AccessPolicy.RequireSelf(SyntheticHarness.Operator,p));
        Assert.Empty((await h.Repo.QueryAsync(RecordPartition.Catalog("demo"),"document",null,null,100,null,default)).Items);
    }
    [Fact]
    public async Task Upload_replay_is_one_document_budget_and_job_and_changed_file_is_not_new_series()
    {
        using var h=new SyntheticHarness();var p=await h.Person();var r=await h.Upload(p);var replay=await h.Upload(p);
        Assert.Equal(r.Body!.ToJsonString(),replay.Body!.ToJsonString());
        Assert.Single((await h.Repo.QueryAsync(RecordPartition.Catalog("demo"),"document",null,null,100,null,default)).Items);
        Assert.Single((await h.Repo.QueryAsync(RecordPartition.Catalog("demo"),"syntheticImportBudget",null,null,100,null,default)).Items);
        var cells=InternalResumeFixture.Cells(count:2);cells["F24"]="≪担当業務≫変更された業務";
        Assert.Equal("TEST_DOCUMENT_LIMIT",(await Assert.ThrowsAsync<DomainException>(()=>h.Upload(p,InternalResumeFixture.Package(cells)))).Code);
        await Assert.ThrowsAsync<DomainException>(()=>h.Upload(p,InternalResumeFixture.Package())); // seven projects
    }
    [Fact]
    public async Task Xlsx_ingestion_enrichment_and_dedicated_search_use_real_operator_not_fake_identity()
    {
        using var h=new SyntheticHarness();var p=await h.Person();var r=await h.Upload(p);
        await h.Processor.RunOnceAsync(default);await h.Processor.RunOnceAsync(default);
        var person=await h.Read(p.Text("id"));Assert.Equal(2,person.Strings("experienceIds").Length);
        var doc=await h.Read(r.Body!["documentId"]!.GetValue<string>());Assert.Equal("complete",doc.Text("aiState"));Assert.True(SyntheticTestAccess.IsTest(doc));
        var catalog=new ValidatedCatalog(new MemoryProjectionRepository(h.Store),new(h.Repo,h.Config),h.Fence,h.Store);
        var request=new SearchRequest([new("domain","software")],[],[]);
        Assert.Empty((await catalog.SearchAsync(request,SyntheticHarness.Operator,default)).Candidates);
        var results=await catalog.SearchAsync(request with{SyntheticOnly=true},SyntheticHarness.Operator,default);
        Assert.Single(results.Candidates);Assert.Equal(person.Text("id"),results.Candidates[0].EntityId);
        Assert.All(results.Candidates[0].Units,u=>Assert.True(u.SyntheticTest));
        await Assert.ThrowsAsync<DomainException>(()=>catalog.SearchAsync(request with{SyntheticOnly=true},SyntheticHarness.Member,default));
        Assert.All(person.Strings("experienceIds"),id=>Assert.StartsWith("experience-",id));
        Assert.Equal("ready",(await h.Read(doc.Text("enrichmentJobId"))).Text("status"));
        var contracts=new List<object>();
        foreach(var kind in new[]{"person","document","documentVersion","evidenceSpan","experience","job","searchUnit","syntheticImportBudget"})
            contracts.AddRange((await h.Store.QueryAsync(RecordPartition.Catalog("demo"),kind,null,null,100,null,default)).Items.Where(r=>SyntheticTestAccess.IsTest(r.Value)).Select(r=>(object)new{schema=RecordJson.ContractKinds[kind],value=r.Value}));
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"DseGrowthLoop.slnx")))root=root.Parent;
        var output=Path.Combine(root!.FullName,"tests/Dse.Tests/TestResults");Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output,"synthetic-test-contract.json"),JsonSerializer.Serialize(contracts));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Revoked_manager_role_or_scope_cancels_pending_import(bool scope)
    {
        using var h=new SyntheticHarness();var p=await h.Person();var r=await h.Upload(p);await h.Revoke(scope);
        await h.Processor.RunOnceAsync(default);
        Assert.Equal("cancelled",(await h.Read(r.Body!["jobId"]!.GetValue<string>())).Text("status"));
        Assert.Empty((await h.Repo.QueryAsync(RecordPartition.Catalog("demo"),"experience",null,null,100,null,default)).Items);
    }
    [Fact]
    public async Task Withdrawal_retains_history_blocks_old_sources_retry_and_reactivation()
    {
        using var h=new SyntheticHarness();var p=await h.Person();var r=await h.Upload(p);await h.Processor.RunOnceAsync(default);await h.Processor.RunOnceAsync(default);
        var person=await h.Read(p.Text("id"));var key=Guid.NewGuid().ToString("N");
        var response=await h.Service.WithdrawAsync(SyntheticHarness.Operator,p.Text("id"),key,person.Text("etag"),"試験終了",default);
        var replay=await h.Service.WithdrawAsync(SyntheticHarness.Operator,p.Text("id"),key,person.Text("etag"),"試験終了",default);Assert.True(JsonNode.DeepEquals(JsonNode.Parse(response.Body!.ToJsonString()),JsonNode.Parse(replay.Body!.ToJsonString())));
        var doc=await h.Read(r.Body!["documentId"]!.GetValue<string>());Assert.Equal("ready",doc.Text("status"));
        var e=await h.Read(person.Strings("experienceIds")[0]);var refs=e["evidenceRefs"]!.Deserialize<EvidenceReference[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(await new SourceGuard(h.Repo,h.Config).EvidenceAsync(SyntheticHarness.Operator,refs[0],default));
        await Assert.ThrowsAsync<DomainException>(()=>h.Upload(p));await Assert.ThrowsAsync<DomainException>(()=>h.Person());
        var dictionary=new SkillDictionaryService(h.Repo,h.Fence,new(h.Repo,TimeProvider.System),TimeProvider.System);
        await Assert.ThrowsAsync<DomainException>(()=>dictionary.CollectAsync(SyntheticHarness.Operator,Guid.NewGuid().ToString("N"),new(e.Text("id")),default));
        Assert.Single((await h.Service.ListUnderFenceAsync(SyntheticHarness.Operator,default))["items"]!.AsArray());
    }
    [Fact]
    public async Task Withdrawal_after_plan_build_prevents_publication()
    {
        using var h=new SyntheticHarness();var p=await h.Person();var r=await h.Upload(p);await h.Processor.RunOnceAsync(default);
        var doc=await h.Read(r.Body!["documentId"]!.GetValue<string>());var job=await h.Read(doc.Text("enrichmentJobId"));var plan=await h.Pipeline.BuildAsync(job,default);
        var person=await h.Read(p.Text("id"));await h.Service.WithdrawAsync(SyntheticHarness.Operator,p.Text("id"),Guid.NewGuid().ToString("N"),person.Text("etag"),"中断",default);
        Assert.Equal("SOURCE_REVOKED",(await Assert.ThrowsAsync<DomainException>(()=>h.Pipeline.CommitAsync(plan,[],default))).Code);
    }
    [Fact]
    public async Task Collection_keeps_test_marker_and_never_changes_dictionary_gold_or_person_confirmation()
    {
        using var h=new SyntheticHarness();var p=await h.Person();var r=await h.Upload(p);await h.Processor.RunOnceAsync(default);await h.Processor.RunOnceAsync(default);
        var person=await h.Read(p.Text("id"));var service=new SkillDictionaryService(h.Repo,h.Fence,new(h.Repo,TimeProvider.System),TimeProvider.System);
        var result=await service.CollectAsync(SyntheticHarness.Operator,Guid.NewGuid().ToString("N"),new(person.Strings("experienceIds")[0]),default);
        var candidateId=result.Body!["bindings"]![0]!["candidateId"]!.GetValue<string>();var candidate=await h.Read(candidateId);Assert.True(SyntheticTestAccess.IsTest(candidate));
        await Assert.ThrowsAsync<DomainException>(()=>service.ReviewAsync(SyntheticHarness.Operator,candidateId,Guid.NewGuid().ToString("N"),candidate.Text("etag"),new("new","検証",0,SkillDictionaryService.EmptyETag,Name:"架空技能",Definition:"架空定義",Boundary:"架空境界"),default));
        Assert.Null(await h.Repo.ReadAsync(RecordPartition.Catalog("demo"),SkillDictionaryService.HeadId,default));
        Assert.Equal("ai_unconfirmed",(await h.Read(p.Text("id"))).Text("confirmationState"));
        Assert.False(RecordJson.ContractKinds.ContainsKey("gold")); // no gold persistence contract/path in this feature
    }
    private static IConfigurationRoot LiveBudgetConfig()
    {
        var config=SkillSemanticTestRuntime.Config();
        foreach(var (key,value) in new Dictionary<string,string>{["Foundry:Endpoint"]="https://synthetic-test.openai.azure.com/",["Foundry:ModelId"]="gpt-6-luna",["Foundry:ModelVersion"]="2026-09-22",["Foundry:Deployment"]="dse-luna6-eval",["Foundry:Region"]="eastus",["Foundry:Sku"]="GlobalStandard",["Foundry:InputUsdPerMillion"]="0.2",["Foundry:OutputUsdPerMillion"]="1.2",["Foundry:OutputLimit"]="4096"})config[key]=value;
        return config;
    }
    [Fact]
    public async Task Immutable_paid_allowance_counts_failed_unknown_calls_and_cannot_be_refilled_by_retry()
    {
        using var h=new SyntheticHarness();var p=await h.Person();var r=await h.Upload(p);var doc=await h.Read(r.Body!["documentId"]!.GetValue<string>());
        var config=LiveBudgetConfig();var budget=SyntheticImportBudget.Create(doc,config,DateTimeOffset.UtcNow);
        var old=await h.Read(budget.Text("id"));await h.Store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(budget,old.Text("etag"))],default);
        Task<AiFailure?> Reserve(string operation,string run)=>h.Fence.RunAsync<AiFailure?>("demo",async ct=>{try{await SyntheticImportBudget.ReserveAsync(h.Repo,config,SyntheticHarness.Operator,[doc.Text("id")],operation,operation=="decide"?ProfilePipeline.PromptVersion:SpecificSkillRules.Version,run,1,ct);return null;}catch(AiException e){return e.Failure;}},default);
        await Reserve("extractSkills","unknown-send-1");
        Assert.Equal(AiFailure.Budget,await Reserve("extractSkills","unknown-send-1"));
        await Reserve("extractSkills","failed-send-2");
        Assert.Equal(AiFailure.Budget,await Reserve("extractSkills","retry-job-3"));
        await Reserve("decide","domain-1");await Reserve("decide","domain-2");
        Assert.Equal(AiFailure.Budget,await Reserve("decide","domain-3"));
        Assert.Equal(4,(await h.Read(budget.Text("id")))["dispatches"]!.AsObject().Count);
        Assert.Equal("Disabled",config["AiUsage:AccountingMode"]);
    }
    [Fact]
    public async Task Budget_pins_model_price_and_io_bounds_and_rejects_extra_operations()
    {
        using var h=new SyntheticHarness();var p=await h.Person();var r=await h.Upload(p);var doc=await h.Read(r.Body!["documentId"]!.GetValue<string>());var config=LiveBudgetConfig();
        var budget=SyntheticImportBudget.Create(doc,config,DateTimeOffset.UtcNow);var old=await h.Read(budget.Text("id"));await h.Store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(budget,old.Text("etag"))],default);
        config["AiUsage:MaxInputTokens"]="10240";
        Assert.Equal(AiFailure.Configuration,(await Assert.ThrowsAsync<AiException>(()=>SyntheticImportBudget.ReserveAsync(h.Repo,config,SyntheticHarness.Operator,[doc.Text("id")],"extractSkills",SpecificSkillRules.Version,"changed",1,default))).Failure);
        Assert.Equal(AiFailure.Budget,(await Assert.ThrowsAsync<AiException>(()=>SyntheticImportBudget.ReserveAsync(h.Repo,config,SyntheticHarness.Operator,[doc.Text("id")],"decide",SkillChoiceMatching.Version,"extra-choice",1,default))).Failure);
        Assert.Empty((await h.Read(budget.Text("id")))["dispatches"]!.AsObject());
    }

    [Fact]
    public async Task Normal_self_upload_remains_self_only_while_synthetic_target_requires_explicit_route()
    {
        using var h=new SyntheticHarness();var p=await h.Person();var actor=SyntheticHarness.Operator;
        var real=RecordJson.New("person",actor.PersonId,"demo",actor.PrincipalId,["demo"],DateTimeOffset.UtcNow);real["displayName"]="本人用fixture";real["departmentId"]="demo";
        var before=await h.Read(actor.PersonId);
        await h.Store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(real,before.Text("etag"))],default);
        var r=await h.Uploads.UploadAsync(actor,"本人資料","profile","self.docx",OfficeExtractor.WordMime,OfficeFixtures.Package(),Guid.NewGuid().ToString("N"),default);
        var doc=await h.Read(r.Body!["documentId"]!.GetValue<string>());Assert.Equal(actor.PersonId,doc.Text("personId"));Assert.False(SyntheticTestAccess.IsTest(doc));
        await Assert.ThrowsAsync<DomainException>(()=>h.Uploads.UploadTestAsync(actor,actor.PersonId,"a.xlsx",OfficeExtractor.ExcelMime,InternalResumeFixture.Package(InternalResumeFixture.Cells(count:2)),Guid.NewGuid().ToString("N"),default));
    }
    [Fact]
    public async Task Queue_failure_after_acceptance_is_recoverable_by_same_upload_without_new_document_or_allowance()
    {
        using var h=new SyntheticHarness();var p=await h.Person();h.Queue.FailSend=true;var r=await h.Upload(p);
        Assert.Equal("pending",(await h.Read(r.Body!["jobId"]!.GetValue<string>())).Text("notificationState"));
        h.Queue.FailSend=false;var replay=await h.Upload(p);Assert.Equal(r.Body!.ToJsonString(),replay.Body!.ToJsonString());
        await h.Processor.RunOnceAsync(default);await h.Processor.RunOnceAsync(default);
        Assert.Equal(2,(await h.Read(p.Text("id"))).Strings("experienceIds").Length);
        Assert.Single((await h.Repo.QueryAsync(RecordPartition.Catalog("demo"),"syntheticImportBudget",null,null,100,null,default)).Items);
    }
    private sealed class LocalOnlyWire : IDispatchTrackingAiTransport
    {
        public int Calls;
        public Task<AiWireResponse> SendAsync(AiModel m,JsonObject b,int max,CancellationToken ct)=>throw new InvalidOperationException("tracking required");
        public Task<AiWireResponse> SendAsync(AiModel m,JsonObject b,int max,CancellationToken ct,AiDispatchState dispatch)
        {
            dispatch.MarkDispatched(ct);Calls++;
            var result=new JsonObject{["model"]=m.ModelId,["choices"]=new JsonArray(new JsonObject{["finish_reason"]="stop",["message"]=new JsonObject{["content"]="{\"state\":\"extracted\",\"items\":[]}",["refusal"]=null}})};
            return Task.FromResult(new AiWireResponse(result,new AiUsage(20,30,false),"local-fake-wire"));
        }
    }
    [Fact]
    public async Task Real_AiService_enforces_test_allowance_with_fake_wire_and_ordinary_ledger_and_audit()
    {
        using var h=new SyntheticHarness();var p=await h.Person();var r=await h.Upload(p);await h.Processor.RunOnceAsync(default);
        var doc=await h.Read(r.Body!["documentId"]!.GetValue<string>());var config=LiveBudgetConfig();var budget=SyntheticImportBudget.Create(doc,config,DateTimeOffset.UtcNow);var old=await h.Read(budget.Text("id"));
        await h.Store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(budget,old.Text("etag"))],default);
        var version=await h.Read(doc.Text("currentVersionId"));var spans=await h.Repo.QueryAsync(RecordPartition.Catalog("demo"),"evidenceSpan","documentVersionId",version.Text("id"),100,null,default);var evidence=spans.Items.First().Value;
        var wire=new LocalOnlyWire();using var ai=new AiService(config,h.Repo,new(h.Repo,config),h.Fence,new(h.Store,TimeProvider.System),wire,TimeProvider.System);
        var context=new AiContext(SyntheticHarness.Operator,"synthetic-test-run",SpecificSkillRules.Version,Retriever.TaxonomyVersion,1,[new(evidence.Text("id"),version.Text("id"),1)],[]);
        var one=await ai.ExtractSkillsAsync(context,"fixture 1",default);Assert.Equal(AiStatus.Succeeded,one.Status);
        Assert.Equal(AiStatus.Succeeded,(await ai.ExtractSkillsAsync(context,"fixture 1",default)).Status);Assert.Equal(1,wire.Calls); // cache doesn't consume a slot
        Assert.Equal(AiStatus.Succeeded,(await ai.ExtractSkillsAsync(context,"fixture 2",default)).Status);
        var stopped=await ai.ExtractSkillsAsync(context,"fixture 3",default);Assert.Equal(AiStatus.BudgetStopped,stopped.Status);Assert.Equal(AiFailure.Budget,stopped.Failure);Assert.Equal(2,wire.Calls);
        Assert.Equal(2,(await h.Read(budget.Text("id")))["dispatches"]!.AsObject().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Previously_resolved_operator_cannot_read_test_sources_after_authority_revocation(bool scope)
    {
        using var h=new SyntheticHarness();var person=await h.Person();var upload=await h.Upload(person);
        var previouslyResolved=await new AuthorityProvider(h.Repo).RequireAsync(SyntheticHarness.Operator,default);
        var doc=await h.Read(upload.Body!["documentId"]!.GetValue<string>());
        await h.Revoke(scope);
        await h.Fence.RunAsync("demo",async ct=>
        {
            await Assert.ThrowsAsync<DomainException>(()=>SyntheticTestAccess.RequireAsync(h.Repo,previouslyResolved,person.Text("id"),ct));
            await Assert.ThrowsAsync<DomainException>(()=>SyntheticTestAccess.RequireLinkedAsync(h.Repo,previouslyResolved,doc,ct));
            return true;
        },default);
    }

}
