using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public sealed class OrganizationAuthorityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ActorAccess Owner = IdentityResolver.Demo;
    private static readonly ActorAccess Guest = IdentityResolver.DemoPerson("demo-person-02")!;
    private static readonly ActorAccess Operator = new("demo","principal-dummy-operator","dummy-operator",["operator"],["demo"]);
    private static string Key() => Guid.NewGuid().ToString("N");
    private sealed class Fake : IKnowledgeDrafter, IDecisionProvider
    {
        public Task<AiResult<KnowledgeSummary>> DraftAsync(AiContext c,CancellationToken ct) => throw new InvalidOperationException("Drafting is not used here");
        public Task<AiResult<Decisions>> DecideAsync(AiContext c,string text,ChoiceQuestion[] choices,NoulQuestion[] nouls,CancellationToken ct) =>
            Task.FromResult(new AiResult<Decisions>(AiStatus.Succeeded,new(choices.ToDictionary(q => q.Id,q => new ChoiceAnswer("software",q.Options.ToDictionary(x=>x.Key,x=>x.Key=="software"?1d:0d),.7)),
                nouls.ToDictionary(q=>q.Id,_=>new NoulAnswer(NoulState.Unknown,null))),"fixture-only",null));
    }
    private sealed record Setup(IRecordStore Store,IPrimaryRepository Repo,AuthorityProvider Authority,OrganizationService Organization,
        ConversationService Chat,KnowledgeService Knowledge,IdentityDisclosureService Names,AnonymousWorkspaceService Workspace,
        BusinessAnalysisService Analysis,string Sid,string Room,string Challenge);
    private static async Task<Setup> Create(IRecordStore? supplied = null)
    {
        var store = supplied ?? new MemoryRecordStore(); var actors = new[]{Owner,Guest,IdentityResolver.DemoPerson("demo-person-03")!,Operator};
        await AuthorityProvider.InitializeAsync(store,"demo",actors.Select(a=>new IdentityBinding("demo","",a.PrincipalId,a.PersonId,a.Roles,true)).ToArray(),actors.Select(a=>new AccessGrant(a.PrincipalId,a.ScopeIds)).ToArray(),default);
        await DemoRecords.SeedAsync(store,"demo",default); await BusinessAnalysisDemoFixtures.SeedAsync(store,default);
        var repo = new PrimaryRepository(store); var authority = new AuthorityProvider(repo); var fence = new AccessFence(repo);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Data:Mode"]="Demo",["Ai:Mode"]="Mock",["AiUsage:Stopped"]="false"}).Build();
        var identities = new IdentityResolver(config); var chat = new ConversationService(repo,new(),identities,TimeProvider.System,fence); var fake = new Fake();
        var knowledge = new KnowledgeService(repo,store,chat,fence,fake,fake,identities,config,TimeProvider.System);
        var workspace = new AnonymousWorkspaceService(repo,new MemoryProjectionRepository(store),new SourceGuard(repo,config),fence,chat,knowledge,identities,config,TimeProvider.System);
        var challenge = await workspace.CreateAsync(Owner,Key(),new("架空の相談","ソフトの引き継ぎについて相談する"),default);
        var room = await workspace.ResolveRoomAsync(Owner,challenge.RoomId!,default);
        var session = await chat.SessionAsync(Owner,room.SessionId,default);
        await chat.InviteAsync(Owner,room.SessionId,Key(),session.ETag,new(Guest.PersonId),true,default);
        var invitation = Assert.Single((await chat.ParticipationsAsync(Guest,room.SessionId,default)).Items);
        await chat.DecideAsync(Guest,room.SessionId,Key(),invitation.ETag,new("accepted"),default);
        return new(store,repo,authority,new(repo,fence,TimeProvider.System),chat,knowledge,new(repo,workspace,chat,fence,config,TimeProvider.System),workspace,
            new(repo,store,fence,new BusinessAnalysisDrafter(config),config,TimeProvider.System),room.SessionId,room.PublicRoomId,challenge.Id);
    }
    private static async Task Share(Setup s)
    {
        foreach(var actor in new[]{Owner,Guest})
        { var view=await s.Names.ReadAsync(actor,s.Room,default); await s.Names.ChangeAsync(actor,s.Room,Key(),view.Etag,new(true,view.ReviewedVersion,view.FieldSet),default); }
        Assert.Equal("shared",(await s.Names.ReadAsync(Owner,s.Room,default)).State);
    }
    private static async Task<JsonObject> Transfer(Setup s,string person = "demo-person-01",bool apply=true,string? key=null)
    {
        var control=await s.Authority.ControlAsync("demo",default); var from=DateTimeOffset.UtcNow.AddHours(-1);
        var row=(await s.Repo.ReadAsync(RecordPartition.Catalog("demo"),AuthorityProvider.PersonId(person),default))!.Value;
        var initial=row["departmentTerms"]!.Deserialize<DepartmentTerm[]>(Json)![0];
        var terms=new[]{initial with{To=from},new DepartmentTerm("transfer-"+person,initial.DepartmentId=="demo-department-a"?"demo-department-b":"demo-department-a",from,null)};
        var input=new JsonObject{["type"]="membership",["change"]=JsonSerializer.SerializeToNode(new MembershipChange(person,terms,"dummy transfer",AuthorityProvider.Token(control.Value)),Json)};
        return await s.Organization.ChangeAsync(Operator,key??Key(),control.ETag,input,apply,default);
    }
    private static async Task Access(Setup s,bool enabled,string[]? roles=null,string[]? scopes=null,string? principal=null)
    {
        var control=await s.Authority.ControlAsync("demo",default);
        await OrganizationCommands.ChangeAccessAsync(s.Repo,Operator,Key(),control.ETag,new(Guest.PersonId,principal??Guest.PrincipalId,"",enabled,
            roles??Guest.Roles,scopes??Guest.ScopeIds,"dummy authority change",AuthorityProvider.Token(control.Value)),default);
    }
    private static async Task<JsonObject> KnowledgeChange(Setup s,string sid,ActorAccess actor,string operation,JsonObject? input=null)
    {
        var state=await s.Knowledge.StateAsync(actor,sid,default); var body=input??new JsonObject();
        if(operation is "request-review" or "consent" or "approve") {body["version"]=state.Number("sourceVersion");body["reviewedVersion"]=state.Text("reviewedVersion");}
        return await s.Knowledge.ChangeAsync(actor,sid,operation,Key(),state.Text("etag"),body,default);
    }
    [Fact] public async Task Transfer_preserves_participation_but_revokes_all_disclosures_and_keeps_occurrence_department()
    {
        var s=await Create(); await Share(s); var before=await s.Authority.PersonAsync("demo",Owner.PersonId,default);
        var request=new BusinessAnalysisRequest("analysis-"+Key(),DateTimeOffset.UtcNow,BusinessAnalysisService.ConfigVersion);
        var run=await s.Analysis.StartAsync(Owner,request,request.RunId,default); Assert.NotEmpty(run["groups"]!.AsArray());
        var old=(await s.Authority.DepartmentSnapshotAsync("demo",Owner.PersonId,DateTimeOffset.UtcNow.AddDays(-1),default))!;
        await Transfer(s);
        Assert.Equal(before!.AccessEpoch,(await s.Authority.PersonAsync("demo",Owner.PersonId,default))!.AccessEpoch);
        await s.Chat.SessionAsync(Owner,s.Sid,default); await s.Chat.SessionAsync(Guest,s.Sid,default);
        Assert.Equal("demo-department-a",(await s.Authority.DepartmentSnapshotAsync("demo",Owner.PersonId,DateTimeOffset.UtcNow.AddDays(-1),default))!.Text("departmentId"));
        Assert.Equal("demo-department-b",(await s.Authority.DepartmentSnapshotAsync("demo",Owner.PersonId,DateTimeOffset.UtcNow,default))!.Text("departmentId"));
        var names=await s.Names.ReadAsync(Owner,s.Room,default); Assert.Equal("pending",names.State); Assert.All(names.Participants,p=>Assert.Null(p.SharedName));
        Assert.Equal("invalidated",(await s.Analysis.GetAsync(Owner,request.RunId,default)).Text("status"));
        Assert.Null(await KnowledgeService.PublishedAsync(s.Repo,"demo","demo-business-session-1",1,default));
        var review=await KnowledgeChange(s,"demo-business-session-1",Owner,"request-review");
        Assert.Null(review["approvedBy"]); Assert.Empty(review["consents"]!.AsObject()); Assert.Equal(2,review.Number("sourceVersion"));
        await KnowledgeChange(s,"demo-business-session-1",Owner,"consent",new(){["publication"]=true,["analysis"]=false});
        Assert.Equal("CONSENT_REQUIRED",(await Assert.ThrowsAsync<DomainException>(()=>KnowledgeChange(s,"demo-business-session-1",Owner,"approve"))).Code);
        await KnowledgeChange(s,"demo-business-session-1",Guest,"consent",new(){["publication"]=true,["analysis"]=false});
        await KnowledgeChange(s,"demo-business-session-1",Owner,"approve");
        Assert.NotNull(await KnowledgeService.PublishedAsync(s.Repo,"demo","demo-business-session-1",2,default));
        Assert.Equal("demo-department-a",review["departmentSnapshot"]!.AsObject().Text("departmentId"));
    }
    [Theory] [InlineData("disabled")] [InlineData("role")] [InlineData("scope")] [InlineData("binding")]
    public async Task Unobserved_authority_A_B_A_never_restores_old_actor_participation_or_consent(string change)
    {
        var s=await Create(); await Share(s); var old=await s.Authority.PersonAsync("demo",Guest.PersonId,default);
        await Access(s,change!="disabled",change=="role"?["analyst"]:null,change=="scope"?["outside"]:null,change=="binding"?"replacement-principal":null);
        // No request observes B before A is restored.
        await Access(s,true);
        Assert.NotEqual(old!.AccessEpoch,(await s.Authority.PersonAsync("demo",Guest.PersonId,default))!.AccessEpoch);
        await Assert.ThrowsAsync<DomainException>(()=>s.Authority.RequireAsync(old,default));
        Assert.Equal(404,(await Assert.ThrowsAsync<DomainException>(()=>s.Chat.SessionAsync(Guest,s.Sid,default))).Status);
        var hidden=await s.Names.ReadAsync(Owner,s.Room,default); Assert.Equal("unavailable",hidden.State); Assert.All(hidden.Participants,p=>Assert.Null(p.SharedName));
        var session=await s.Chat.SessionAsync(Owner,s.Sid,default);
        await s.Chat.InviteAsync(Owner,s.Sid,Key(),session.ETag,new(Guest.PersonId),true,default);
        var invitation=Assert.Single((await s.Chat.ParticipationsAsync(Guest,s.Sid,default)).Items);
        Assert.Equal("requested",invitation.Value.Text("status"));
        await s.Chat.DecideAsync(Guest,s.Sid,Key(),invitation.ETag,new("accepted"),default);
        await s.Chat.SessionAsync(Guest,s.Sid,default);
        var pending=await s.Names.ReadAsync(Owner,s.Room,default); Assert.Equal("pending",pending.State); Assert.All(pending.Participants,p=>Assert.Null(p.SharedName));
    }
    [Fact] public async Task Old_confirmation_and_consent_receipt_do_not_attach_to_a_new_authority()
    {
        var s=await Create(); const string sid="demo-business-session-1"; var state=await s.Knowledge.StateAsync(Owner,sid,default);
        var key=Key();var body=new JsonObject{["version"]=state.Number("sourceVersion"),["reviewedVersion"]=state.Text("reviewedVersion"),["publication"]=true,["analysis"]=true};
        await s.Knowledge.ChangeAsync(Owner,sid,"consent",key,state.Text("etag"),body,default);
        await Transfer(s);
        var replay=await s.Knowledge.ChangeAsync(Owner,sid,"consent",key,state.Text("etag"),body,default);
        Assert.Empty(replay["consents"]!.AsObject()); Assert.Equal("draft",replay.Text("status"));
        Assert.Null(await KnowledgeService.PublishedAsync(s.Repo,"demo",sid,1,default));
        var now=await s.Knowledge.StateAsync(Owner,sid,default);
        Assert.Equal("STALE_APPROVAL",(await Assert.ThrowsAsync<DomainException>(()=>s.Knowledge.ChangeAsync(Owner,sid,"approve",Key(),now.Text("etag"),new(){["version"]=1,["reviewedVersion"]=state.Text("reviewedVersion")},default))).Code);
    }
    [Fact] public async Task Generation_reset_blocks_restored_sessions_and_all_old_grants_without_reassigning_roles()
    {
        var s=await Create(); await Share(s); var old=await s.Authority.PersonAsync("demo",Owner.PersonId,default);
        var control=await s.Authority.ControlAsync("demo",default);
        await OrganizationCommands.ResetGenerationAsync(s.Repo,Operator,control.ETag,"restored dummy store",default);
        Assert.NotEqual(old!.AuthorityToken,AuthorityProvider.Token((await s.Authority.ControlAsync("demo",default)).Value));
        Assert.Null(await s.Authority.PersonAsync("demo",Owner.PersonId,default));
        await Assert.ThrowsAsync<DomainException>(()=>s.Chat.SessionAsync(Owner,s.Sid,default));
        await Assert.ThrowsAsync<DomainException>(()=>s.Names.ReadAsync(Owner,s.Room,default));
        Assert.Null(await KnowledgeService.PublishedAsync(s.Repo,"demo","demo-business-session-1",1,default));
        await DemoRecords.SeedAsync(s.Store,"demo",default);
        Assert.Null(await s.Authority.PersonAsync("demo",Owner.PersonId,default));
        await Assert.ThrowsAsync<DomainException>(()=>s.Chat.SessionAsync(Owner,s.Sid,default));
    }
    [Fact] public async Task Unknown_history_is_not_inferred_from_a_valid_looking_department_id()
    {
        var s=await Create(); var person=(await s.Repo.ReadAsync(RecordPartition.Catalog("demo"),AuthorityProvider.PersonId(Owner.PersonId),default))!;
        person.Value["departmentTerms"]=new JsonArray(); await s.Repo.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(person.Value,person.ETag)],default);
        Assert.Null(await s.Authority.DepartmentSnapshotAsync("demo",Owner.PersonId,DateTimeOffset.UtcNow,default));
        Assert.False((await s.Organization.SelfAsync(Owner,default))["verified"]!.GetValue<bool>());
        var request=new BusinessAnalysisRequest("analysis-"+Key(),DateTimeOffset.UtcNow,BusinessAnalysisService.ConfigVersion);
        var result=await s.Analysis.StartAsync(Owner,request,request.RunId,default); Assert.Empty(result["groups"]!.AsArray());
    }
    [Fact] public async Task Validation_does_not_write_and_operator_is_not_automatically_granted_to_members()
    {
        var s=await Create();var before=await s.Authority.ControlAsync("demo",default); await Transfer(s,apply:false);
        Assert.Equal(before.ETag,(await s.Authority.ControlAsync("demo",default)).ETag);
        Assert.Equal(403,(await Assert.ThrowsAsync<DomainException>(()=>s.Organization.ReadAsync(Owner,default))).Status);
        var self=await s.Organization.SelfAsync(Owner,default); Assert.DoesNotContain("personId",self.ToJsonString()); Assert.DoesNotContain("scopeIds",self.ToJsonString());
    }
    [Theory] [InlineData("overlap")] [InlineData("unknown")] [InlineData("future")] [InlineData("unknown-field")]
    public async Task Invalid_terms_and_unknown_fields_fail_before_authority_revision_changes(string condition)
    {
        var s=await Create();var control=await s.Authority.ControlAsync("demo",default);var now=DateTimeOffset.UtcNow.AddDays(-1);
        var terms=new JsonArray(JsonSerializer.SerializeToNode(new DepartmentTerm("term-a",condition=="unknown"?"invented":"demo-department-a",condition=="future"?now.AddDays(3):now,null),Json));
        if(condition=="overlap") terms.Add(JsonSerializer.SerializeToNode(new DepartmentTerm("term-b","demo-department-b",now,null),Json));
        if(condition=="unknown-field") terms[0]!["roles"]=new JsonArray("operator");
        var input=new JsonObject{["type"]="membership",["change"]=new JsonObject{["personId"]=Owner.PersonId,["terms"]=terms,["reason"]="invalid fixture",["expectedAuthorityToken"]=AuthorityProvider.Token(control.Value)}};
        await Assert.ThrowsAsync<ArgumentException>(()=>s.Organization.ChangeAsync(Operator,Key(),control.ETag,input,true,default));
        Assert.Equal(control.ETag,(await s.Authority.ControlAsync("demo",default)).ETag);
        Assert.True(string.IsNullOrEmpty((await s.Repo.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",default))!.Value.Text("holder")));
    }
    [Fact] public async Task Missing_or_maintenance_authority_never_falls_back_to_demo_configuration()
    {
        var empty=new AuthorityProvider(new PrimaryRepository(new MemoryRecordStore()));
        Assert.Equal("AUTHORITY_UNAVAILABLE",(await Assert.ThrowsAsync<DomainException>(()=>empty.RequireAsync(Owner,default))).Code);
        var s=await Create();var row=await s.Authority.ControlAsync("demo",default);row.Value["state"]="maintenance";
        await s.Repo.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(row.Value,row.ETag)],default);
        await Assert.ThrowsAsync<DomainException>(()=>s.Chat.SessionAsync(Owner,s.Sid,default));
        await Assert.ThrowsAsync<DomainException>(()=>s.Names.ReadAsync(Owner,s.Room,default));
    }
    [Fact] public async Task Stopped_commands_reject_production_or_unconfirmed_use_before_opening_storage()
    {
        var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Data:Mode"]="Azure",["Data:LocalPath"]="/not-opened"}).Build();
        Assert.Equal("LOCAL_ORGANIZATION_STOP_REQUIRED",Assert.Throws<DomainException>(()=>OrganizationCommands.Validate(cfg,true)).Code);
        cfg["Data:Mode"]="Demo"; Assert.Throws<DomainException>(()=>OrganizationCommands.Validate(cfg,true));
    }
    private sealed class FaultStore : IRecordStore
    {
        private readonly MemoryRecordStore inner=new();
        public bool FailChange,AfterCommit,FailGeneration;
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)=>inner.ReadAsync(p,id,ct);
        public Task<RecordPage> QueryAsync(RecordPartition p,string kind,string? field,string? parent,int size,string? cursor,CancellationToken ct)=>inner.QueryAsync(p,kind,field,parent,size,cursor,ct);
        public async Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)
        {
            var fail=FailChange && writes.Any(w=>w.Value.Text("kind")=="personAuthority") ||
                FailGeneration && writes.Any(w=>w.Value.Text("kind")=="authorityControl" && w.Value.Text("state")=="active");
            if(fail && !AfterCommit) throw new IOException("fixture batch failure");
            await inner.CommitAsync(p,writes,ct);
            if(fail) throw new IOException("fixture acknowledgement lost");
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Organization_batch_failure_or_lost_ack_does_not_split_control_and_terms_or_duplicate_change(bool afterCommit)
    {
        var store=new FaultStore();var s=await Create(store);var control=await s.Authority.ControlAsync("demo",default);
        var before=(await s.Repo.ReadAsync(RecordPartition.Catalog("demo"),AuthorityProvider.PersonId(Guest.PersonId),default))!;
        var terms=before.Value["departmentTerms"]!.DeepClone();
        var input=new JsonObject{["type"]="membership",["change"]=new JsonObject{["personId"]=Guest.PersonId,["terms"]=terms,["reason"]="atomic fixture",["expectedAuthorityToken"]=AuthorityProvider.Token(control.Value)}};
        var key=Key();store.FailChange=true;store.AfterCommit=afterCommit;
        await Assert.ThrowsAsync<IOException>(()=>s.Organization.ChangeAsync(Operator,key,control.ETag,input,true,default));
        Assert.Equal(afterCommit?2:1,(await s.Authority.ControlAsync("demo",default)).Value.Number("revision"));
        Assert.Equal(afterCommit?2:1,(await s.Repo.ReadAsync(RecordPartition.Catalog("demo"),AuthorityProvider.PersonId(Guest.PersonId),default))!.Value.Number("sourceVersion"));
        Assert.Equal("ACCESS_BUSY",(await Assert.ThrowsAsync<DomainException>(()=>s.Organization.ChangeAsync(Operator,key,control.ETag,input,true,default))).Code);
        store.FailChange=false;
        var held=(await s.Repo.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",default))!;
        await new FenceRecovery(store).RecoverAsync("demo",held.ETag,[],default);
        await s.Organization.ChangeAsync(Operator,key,control.ETag,input,true,default);
        Assert.Equal(2,(await s.Authority.ControlAsync("demo",default)).Value.Number("revision"));
    }
    [Fact] public async Task Failed_generation_rotation_leaves_maintenance_closed_across_normal_seed_and_restart()
    {
        var store=new FaultStore();var s=await Create(store);var control=await s.Authority.ControlAsync("demo",default);store.FailGeneration=true;
        await Assert.ThrowsAsync<IOException>(()=>OrganizationCommands.ResetGenerationAsync(s.Repo,Operator,control.ETag,"fixture restore",default));
        Assert.Equal("maintenance",(await s.Repo.ReadAsync(RecordPartition.Catalog("demo"),AuthorityProvider.ControlId,default))!.Value.Text("state"));
        store.FailGeneration=false;await DemoRecords.SeedAsync(store,"demo",default);
        await Assert.ThrowsAsync<DomainException>(()=>new AuthorityProvider(new PrimaryRepository(store)).RequireAsync(Owner,default));
        await Assert.ThrowsAsync<DomainException>(()=>s.Chat.SessionAsync(Guest,s.Sid,default));
    }
    [Fact] public async Task Response_consumption_blocks_authority_change_until_serialization_finishes()
    {
        var s=await Create();await Share(s);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading=s.Names.ConsumeAsync(Owner,s.Room,async (view,_)=>{Assert.Equal("shared",view.State);entered.SetResult();await release.Task;return true;},default);
        await entered.Task;
        try { Assert.Equal("ACCESS_BUSY",(await Assert.ThrowsAsync<DomainException>(()=>Transfer(s))).Code); }
        finally { release.SetResult(); }
        await reading;await Transfer(s);
        Assert.All((await s.Names.ReadAsync(Owner,s.Room,default)).Participants,p=>Assert.Null(p.SharedName));
    }
    [Fact] public async Task Candidate_token_does_not_survive_an_unobserved_retirement_and_reenable()
    {
        var s=await Create();var candidates=await s.Workspace.ConsumeAsync(Owner,ct=>s.Workspace.CandidatesUnderFenceAsync(Owner,
            // The public challenge belongs to this room, never a caller-supplied internal id.
            s.Challenge,ct),default);
        var candidate=Assert.Single(candidates.Items);
        var saved=(await s.Repo.ReadAsync(RecordPartition.Catalog("demo"),candidate.Token,default))!;
        var pid=saved.Value["unit"]!["entityId"]!.GetValue<string>();var person=(await s.Authority.PersonAsync("demo",pid,default))!;
        foreach(var enabled in new[]{false,true})
        {
            var control=await s.Authority.ControlAsync("demo",default);
            await OrganizationCommands.ChangeAccessAsync(s.Repo,Operator,Key(),control.ETag,new(pid,person.PrincipalId,"",enabled,person.Roles,person.ScopeIds,"dummy candidate change",AuthorityProvider.Token(control.Value)),default);
        }
        var room=await s.Workspace.ResolveRoomAsync(Owner,s.Room,default);
        Assert.Equal("SOURCE_REVOKED",(await Assert.ThrowsAsync<DomainException>(()=>s.Workspace.InviteAsync(Owner,s.Room,Key(),room.SessionEtag,new(candidate.Token),default))).Code);
    }
    [Fact] public async Task Inherited_nested_work_cannot_escape_the_lifetime_of_its_durable_fence()
    {
        var repo=new PrimaryRepository(new MemoryRecordStore());var fence=new AccessFence(repo);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Task? nested=null;
        var root=fence.RunAsync("demo",async ct=>
        {nested=Task.Run(()=>fence.RunAsync("demo",async _=>{started.SetResult();await release.Task;return true;},ct,nested:true));await started.Task;return true;},default);
        await started.Task;
        Assert.Equal("ACCESS_BUSY",(await Assert.ThrowsAsync<DomainException>(()=>new AccessFence(repo).RunAsync("demo",_=>Task.FromResult(true),default))).Code);
        Assert.False(root.IsCompleted);release.SetResult();await root;await nested!;
        Assert.True(await fence.RunAsync("demo",_=>Task.FromResult(true),default));
    }
    [Fact] public async Task Same_oid_with_different_letter_case_cannot_bind_two_people()
    {
        var s=await Create();var control=await s.Authority.ControlAsync("demo",default);
        await OrganizationCommands.ChangeAccessAsync(s.Repo,Operator,Key(),control.ETag,new(Guest.PersonId,Guest.PrincipalId,
            "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA",true,Guest.Roles,Guest.ScopeIds,"dummy binding",AuthorityProvider.Token(control.Value)),default);
        control=await s.Authority.ControlAsync("demo",default);
        await Assert.ThrowsAsync<ArgumentException>(()=>OrganizationCommands.ChangeAccessAsync(s.Repo,Operator,Key(),control.ETag,new(Owner.PersonId,Owner.PrincipalId,
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",true,Owner.Roles,Owner.ScopeIds,"duplicate binding",AuthorityProvider.Token(control.Value)),default));
        Assert.Equal(control.ETag,(await s.Authority.ControlAsync("demo",default)).ETag);
    }
    [Fact] public async Task Claimed_department_ids_never_inflate_the_authoritative_department_count()
    {
        var s=await Create();
        for(var i=1;i<=3;i++)
        {
            var sid="demo-business-session-"+i;var p=RecordPartition.Conversation("demo",sid);var id=KnowledgeService.Id(sid);
            var original=(await s.Repo.ReadAsync(p,id,default))!;var published=(await s.Repo.ReadAsync(RecordPartition.Catalog("demo"),id,default))!;
            original.Value["analysisMetadata"]!["departmentId"]="invented-"+i;
            published.Value["analysisMetadata"]!["departmentId"]="invented-"+i;
            await s.Repo.CommitAsync(p,[RecordWrite.Replace(original.Value,original.ETag)],default);
            await s.Repo.CommitAsync(RecordPartition.Catalog("demo"),[RecordWrite.Replace(published.Value,published.ETag)],default);
        }
        var request=new BusinessAnalysisRequest("analysis-"+Key(),DateTimeOffset.UtcNow,BusinessAnalysisService.ConfigVersion);
        var result=await s.Analysis.StartAsync(Owner,request,request.RunId,default);
        Assert.Equal(2,result["groups"]![0]!["metrics"]!["distinctDepartments"]!.GetValue<int>());
        Assert.DoesNotContain("invented-",result.ToJsonString());
    }
    [Theory] [InlineData("type")] [InlineData("reason")] [InlineData("expectedAuthorityToken")] [InlineData("reviewedVersion")]
    public async Task Malformed_json_types_are_input_errors_and_cannot_poison_the_tenant_fence(string field)
    {
        var s=await Create();var control=await s.Authority.ControlAsync("demo",default);
        if(field=="reviewedVersion")
        {
            var state=await s.Knowledge.StateAsync(Owner,"demo-business-session-1",default);
            await Assert.ThrowsAsync<ArgumentException>(()=>s.Knowledge.ChangeAsync(Owner,"demo-business-session-1","approve",Key(),state.Text("etag"),
                new(){["version"]=1,["reviewedVersion"]=123},default));
        }
        else
        {
            var person=(await s.Repo.ReadAsync(RecordPartition.Catalog("demo"),AuthorityProvider.PersonId(Owner.PersonId),default))!;
            var body=new JsonObject{["personId"]=Owner.PersonId,["terms"]=person.Value["departmentTerms"]!.DeepClone(),["reason"]="dummy change",["expectedAuthorityToken"]=AuthorityProvider.Token(control.Value)};
            var input=new JsonObject{["type"]="membership",["change"]=body};
            if(field=="type") input["type"]=123; else body[field]=123;
            await Assert.ThrowsAsync<ArgumentException>(()=>s.Organization.ChangeAsync(Operator,Key(),control.ETag,input,true,default));
        }
        Assert.Equal(control.ETag,(await s.Authority.ControlAsync("demo",default)).ETag);
        Assert.True(string.IsNullOrEmpty((await s.Repo.ReadAsync(RecordPartition.Catalog("demo"),"system-access-fence",default))!.Value.Text("holder")));
        await s.Chat.SessionAsync(Owner,s.Sid,default);
    }
}
