using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dse.Tests;

public sealed class ProfileEvidenceReadContextTests
{
    private sealed class ObservedRepository(IPrimaryRepository inner):IPrimaryRepository
    {
        public Func<string,CancellationToken,Task>? BeforeRead;public Func<string,bool>? Missing;
        public Dictionary<string,int> Reads {get;}=[];
        public async Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)
        {Reads[id]=Reads.GetValueOrDefault(id)+1;if(BeforeRead is not null)await BeforeRead(id,ct);return Missing?.Invoke(id)==true?null:await inner.ReadAsync(p,id,ct);}
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> w,CancellationToken ct)=>inner.CommitAsync(p,w,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string k,string? f,string? parent,int n,string? token,CancellationToken ct)=>inner.QueryAsync(p,k,f,parent,n,token,ct);
    }
    private sealed class BoundaryAi(IPrimaryRepository repo):IWorkbookStructurer,ITextStructurer,IDecisionProvider,IConcreteSkillExtractor
    {
        private readonly PipelineAi decisions=new();public Func<AiContext,Task>? AfterStructure,AfterSkills;
        public int Structures,Decisions,Skills;
        public async Task<AiResult<WorkbookStructure>> StructureWorkbookAsync(AiContext c,CancellationToken ct)
        {Structures++;var result=await new FixtureWorkbookSections(repo).StructureWorkbookAsync(c,ct);if(AfterStructure is not null)await AfterStructure(c);return result;}
        public Task<AiResult<StructuredText>> StructureAsync(AiContext c,string text,CancellationToken ct)=>decisions.StructureAsync(c,text,ct);
        public Task<AiResult<Decisions>> DecideAsync(AiContext c,string text,ChoiceQuestion[] choices,NoulQuestion[] nouls,CancellationToken ct)
        {Decisions++;return decisions.DecideAsync(c,text,choices,nouls,ct);}
        public async Task<AiResult<SpecificSkills>> ExtractSkillsAsync(AiContext c,string text,CancellationToken ct)
        {Skills++;var result=await new SyntheticFixtureSkills().ExtractSkillsAsync(c,text,ct);if(AfterSkills is not null)await AfterSkills(c);return result;}
    }
    private static async Task<(JsonObject Person,JsonObject Original,JsonObject Generation,JsonObject Job)> Prepared(SyntheticHarness h)
    {
        var person=await h.Person();var upload=await h.Upload(person);await h.Processor.RunOnceAsync(default);await h.Processor.RunOnceAsync(default);
        person=await h.Read(person.Text("id"));var original=await h.Read(upload.Body!["documentId"]!.GetValue<string>());
        var service=new ReextractionGenerations(h.Repo,h.Fence,new(h.Repo,TimeProvider.System),h.Notifications,h.Config,TimeProvider.System);
        var estimate=await service.EstimateAsync(SyntheticHarness.Operator,person.Text("id"),original.Text("id"),default);
        var accepted=await service.StartAsync(SyntheticHarness.Operator,person.Text("id"),original.Text("id"),new(estimate.Text("estimateId"),estimate["maximumUsd"]!.GetValue<decimal>(),"isolated context regression"),Guid.NewGuid().ToString("N"),estimate.Text("sourceEtag"),default);
        await h.Processor.RunOnceAsync(default);var generation=await h.Read(accepted.Body!["generationId"]!.GetValue<string>());
        return(person,original,generation,await h.Read(generation.Text("enrichmentJobId")));
    }
    private static ProfilePipeline Pipeline(SyntheticHarness h,IPrimaryRepository repo,AccessFence fence,BoundaryAi ai,TimeProvider? clock=null)=>
        new(repo,h.Store,fence,ai,ai,new(h.Config),h.Config,clock??TimeProvider.System,ai,ai);
    private static async Task<string> Projections(SyntheticHarness h,string personId)=>RecordJson.Canonical(JsonSerializer.SerializeToNode((await h.Store.QueryAsync(RecordPartition.Catalog("demo"),"searchUnit","entityId",personId,100,null,default)).Items.Select(r=>r.Value).ToArray()));
    [Fact]
    public async Task One_phase_reads_shared_versions_once_and_preserves_reference_hash_etags_and_order()
    {
        using var h=new SyntheticHarness();var (_,_,generation,_)=await Prepared(h);var p=RecordPartition.Catalog("demo");
        var refs=(await h.Repo.QueryAsync(p,"evidenceSpan","documentId",generation.Text("id"),100,null,default)).Items.Take(3).Select(r=>new EvidenceReference(r.Value.Text("id"),r.Value.Text("documentVersionId"),r.Value.Number("sourceVersion"))).ToArray();
        var repo=new ObservedRepository(h.Repo);var fence=new AccessFence(repo);Dictionary<string,string?> expected=[];
        var snapshots=await fence.PrepareThenRunAsync("demo",(reads,ct)=>new ProfileEvidenceReadContext(reads,p,expected,h.Config).CaptureAsync(SyntheticHarness.Operator,[..refs,refs[0]],ct),(r,ct)=>Task.FromResult(r),default);
        Assert.Equal(4,snapshots.Length);Assert.Equal(refs[0],snapshots[3].Reference);
        foreach(var id in refs.Select(r=>r.Id).Append(refs[0].DocumentVersionId).Append(generation.Text("id")).Append(AuthorityProvider.ControlId))Assert.Equal(1,repo.Reads[id]);
        foreach(var s in snapshots){Assert.Equal(expected[s.Reference.Id],s.SpanEtag);Assert.Equal(expected[s.Reference.DocumentVersionId],s.VersionEtag);Assert.Equal(expected[generation.Text("id")],s.DocumentEtag);Assert.Equal(OfficeExtractor.Hash(Encoding.UTF8.GetBytes(s.Source.Text)),s.TextSha256);}
        Assert.Null((await h.Read("system-access-fence"))["holder"]);
    }
    [Theory]
    [InlineData("role")][InlineData("scope")][InlineData("version")][InlineData("parent")][InlineData("missing")]
    public async Task Change_during_AI_is_checked_by_the_next_phase_without_more_sends_or_publication(string mode)
    {
        using var h=new SyntheticHarness();var (person,original,generation,job)=await Prepared(h);var oldHead=(await h.Read(person.Text("id")))["documentHeads"]!.ToJsonString();var oldProjection=await Projections(h,person.Text("id"));var budget=await h.Read(SyntheticImportBudget.Id(original.Text("id")));
        var repo=new ObservedRepository(h.Repo);var fence=new AccessFence(repo);var ai=new BoundaryAi(h.Repo);
        if(mode is "role" or "scope")ai.AfterStructure=_=>h.Revoke(mode=="scope");
        else ai.AfterSkills=async c=>
        {
            if(mode=="missing"){var missing=c.Evidence[0].Id;repo.Missing=id=>id==missing;return;}
            await h.Fence.RunAsync("demo",async ct=>{var target=await h.Read(mode=="parent"?original.Text("id"):c.Evidence[0].DocumentVersionId);target["updatedAt"]=DateTimeOffset.UtcNow;await h.Store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(target,target.Text("etag"))],ct);return true;},default);
        };
        var pipeline=Pipeline(h,repo,fence,ai);var worker=new IngestionProcessor(repo,h.Blobs,h.Queue,new OfficeExtractor(new()),new(){Attempts=1},fence,TimeProvider.System,NullLogger<IngestionProcessor>.Instance,pipeline);
        await worker.RunOnceAsync(default);
        Assert.Equal("cancelled",(await h.Read(job.Text("id"))).Text("status"));Assert.Equal("SOURCE_REVOKED",(await h.Read(job.Text("id"))).Text("lastErrorCode"));Assert.NotEqual("published",(await h.Read(generation.Text("id"))).Text("generationState"));
        Assert.Equal(oldHead,(await h.Read(person.Text("id")))["documentHeads"]!.ToJsonString());Assert.Equal(oldProjection,await Projections(h,person.Text("id")));Assert.Equal(budget.ToJsonString(),(await h.Read(budget.Text("id"))).ToJsonString());
        Assert.Equal(mode is "role" or "scope"?0:1,ai.Decisions);Assert.Equal(mode is "role" or "scope"?0:1,ai.Skills);Assert.Null((await h.Read("system-access-fence"))["holder"]);
    }
    [Fact]
    public async Task Concurrent_writer_cannot_enter_phase_and_change_after_phase_is_rejected_at_commit()
    {
        using var h=new SyntheticHarness();var (person,original,generation,job)=await Prepared(h);var oldHead=(await h.Read(person.Text("id")))["documentHeads"]!.ToJsonString();var before=await Projections(h,person.Text("id"));
        var repo=new ObservedRepository(h.Repo);bool attempted=false,entered=false;
        repo.BeforeRead=async(id,ct)=>{if(attempted||!FencedReadPhase.IsActive||id!=generation.Text("id"))return;attempted=true;var error=await Assert.ThrowsAsync<DomainException>(()=>h.Fence.RunAsync("demo",t=>{entered=true;return Task.FromResult(true);},ct));Assert.Equal("ACCESS_BUSY",error.Code);};
        var fence=new AccessFence(repo);var ai=new BoundaryAi(h.Repo);var pipeline=Pipeline(h,repo,fence,ai);var plan=await pipeline.BuildAsync(job,default);Assert.True(attempted);Assert.False(entered);
        await h.Fence.RunAsync("demo",async ct=>{var row=await h.Read(original.Text("id"));row["updatedAt"]=DateTimeOffset.UtcNow;await h.Store.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(row,row.Text("etag"))],ct);return true;},default);
        Assert.Equal("SOURCE_REVOKED",(await Assert.ThrowsAsync<DomainException>(()=>pipeline.CommitAsync(plan,[],default))).Code);
        Assert.Equal(oldHead,(await h.Read(person.Text("id")))["documentHeads"]!.ToJsonString());Assert.Equal(before,await Projections(h,person.Text("id")));Assert.Null((await h.Read("system-access-fence"))["holder"]);
    }
    [Fact]
    public async Task Deadline_in_new_read_only_phase_keeps_old_search_and_releases_only_after_read_finishes()
    {
        using var h=new SyntheticHarness();var (person,original,generation,job)=await Prepared(h);var head=(await h.Read(person.Text("id")))["documentHeads"]!.ToJsonString();var projection=await Projections(h,person.Text("id"));
        var clock=new ManualPipelineClock();var repo=new ObservedRepository(h.Repo);var once=false;
        repo.BeforeRead=async(id,ct)=>{if(!once&&FencedReadPhase.IsActive&&id.StartsWith(generation.Text("currentVersionId")+"-e",StringComparison.Ordinal)){once=true;await Task.Delay(TimeSpan.FromSeconds(121),clock,ct);}};
        var fence=new AccessFence(repo);var ai=new BoundaryAi(h.Repo);var worker=new IngestionProcessor(repo,h.Blobs,h.Queue,new OfficeExtractor(new()),new(){ProcessingSeconds=120,Attempts=1},fence,clock,NullLogger<IngestionProcessor>.Instance,Pipeline(h,repo,fence,ai,clock));
        await clock.Drive(worker.RunOnceAsync(default));var end=await h.Read(job.Text("id"));Assert.Equal("PROCESSING_TIMEOUT",end.Text("lastErrorCode"));Assert.Equal("failed",end.Text("status"));Assert.Equal(0,ai.Decisions);Assert.Equal(0,ai.Skills);
        Assert.Equal(head,(await h.Read(person.Text("id")))["documentHeads"]!.ToJsonString());Assert.Equal(projection,await Projections(h,person.Text("id")));Assert.Null((await h.Read("system-access-fence"))["holder"]);
    }
    [Fact]
    public async Task Read_with_uncertain_completion_keeps_fence_until_it_has_drained()
    {
        using var h=new SyntheticHarness();var (_,_,generation,_)=await Prepared(h);var p=RecordPartition.Catalog("demo");var row=(await h.Repo.QueryAsync(p,"evidenceSpan","documentId",generation.Text("id"),100,null,default)).Items[0];
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var pending=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var repo=new ObservedRepository(h.Repo);using var cancel=new CancellationTokenSource();
        repo.BeforeRead=async(id,ct)=>{if(id==row.Value.Text("id")){entered.TrySetResult();await pending.Task;}};var fence=new AccessFence(repo);Dictionary<string,string?> expected=[];
        var work=fence.PrepareThenRunAsync("demo",(reads,ct)=>new ProfileEvidenceReadContext(reads,p,expected,h.Config).CaptureAsync(SyntheticHarness.Operator,[new(row.Value.Text("id"),row.Value.Text("documentVersionId"),row.Value.Number("sourceVersion"))],ct),(r,ct)=>Task.FromResult(r),cancel.Token);
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();Assert.False(work.IsCompleted);Assert.NotNull((await h.Read("system-access-fence"))["holder"]);pending.SetResult();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>work);Assert.Null((await h.Read("system-access-fence"))["holder"]);}finally{pending.TrySetResult();}
    }
    [Theory]
    [InlineData("version")][InlineData("document")][InlineData("tenant")][InlineData("otherDocument")]
    public async Task A_cached_id_never_skips_reference_version_or_tenant_validation(string mode)
    {
        using var h=new SyntheticHarness();var (_,original,generation,_)=await Prepared(h);var p=RecordPartition.Catalog("demo");
        var row=(await h.Repo.QueryAsync(p,"evidenceSpan","documentId",generation.Text("id"),100,null,default)).Items[0];
        var valid=new EvidenceReference(row.Value.Text("id"),row.Value.Text("documentVersionId"),row.Value.Number("sourceVersion"));
        var invalid=mode=="version"?valid with{SourceVersion=valid.SourceVersion+1}:valid with{DocumentVersionId=mode=="otherDocument"?original.Text("currentVersionId"):"missing-version"};
        var actor=mode=="tenant"?SyntheticHarness.Operator with{TenantId="other"}:SyntheticHarness.Operator;Dictionary<string,string?> expected=[];
        await Assert.ThrowsAsync<DomainException>(()=>h.Fence.PrepareThenRunAsync("demo",(reads,ct)=>new ProfileEvidenceReadContext(reads,p,expected,h.Config).CaptureAsync(actor,[valid,invalid],ct),(r,ct)=>Task.FromResult(r),default));
        Assert.Null((await h.Read("system-access-fence"))["holder"]);
    }

    [Fact]
    public async Task Same_scope_different_operator_cannot_borrow_a_previous_capture()
    {
        using var h=new SyntheticHarness();var (_,_,generation,_)=await Prepared(h);var p=RecordPartition.Catalog("demo");
        var other= SyntheticHarness.Other with{ScopeIds=["demo"]};var authority=(await h.Repo.ReadAsync(p,AuthorityProvider.PersonId(other.PersonId),default))!;
        authority.Value["scopeIds"]=RecordJson.Array(["demo"]);await h.Store.CommitAsync(p,[RecordWrite.Replace(authority.Value,authority.ETag)],default);
        var row=(await h.Repo.QueryAsync(p,"evidenceSpan","documentId",generation.Text("id"),100,null,default)).Items[0];
        var reference=new EvidenceReference(row.Value.Text("id"),row.Value.Text("documentVersionId"),row.Value.Number("sourceVersion"));Dictionary<string,string?> expected=[];
        await Assert.ThrowsAsync<DomainException>(()=>h.Fence.PrepareThenRunAsync("demo",async(reads,ct)=>
        {
            var context=new ProfileEvidenceReadContext(reads,p,expected,h.Config);
            Assert.Single(await context.CaptureAsync(SyntheticHarness.Operator,[reference],ct));
            return await context.CaptureAsync(other,[reference],ct);
        },(r,ct)=>Task.FromResult(r),default));
        Assert.Null((await h.Read("system-access-fence"))["holder"]);
    }

}
