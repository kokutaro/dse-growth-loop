using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dse.Tests;

public sealed class AnonymousWorkspaceTests
{
    private static string Key() => Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class FakeAi : IKnowledgeDrafter,IDecisionProvider
    {
        public Task<AiResult<KnowledgeSummary>> DraftAsync(AiContext c,CancellationToken ct) => Task.FromResult(new AiResult<KnowledgeSummary>(AiStatus.Succeeded,
            new(KnowledgeSummary.Fields.Select(f => new SummarySection(f,"ソフト 引き継ぎを試した","participant_report",[c.ConversationEvidenceIds![0]])).ToArray()),"fixture-run",null));
        public Task<AiResult<Decisions>> DecideAsync(AiContext c,string text,ChoiceQuestion[] choices,NoulQuestion[] nouls,CancellationToken ct) => Task.FromResult(new AiResult<Decisions>(AiStatus.Succeeded,
            new(choices.ToDictionary(q => q.Id,q => new ChoiceAnswer("software",q.Options.ToDictionary(x => x.Key,x => x.Key == "software" ? 1d : 0d),.7)),
                nouls.ToDictionary(q => q.Id,_ => new NoulAnswer(NoulState.Unknown,null))),"fixture-classify",null));
    }
    private sealed record Setup(IPrimaryRepository Repo,IRecordStore Store,AccessFence Fence,ConversationService Chat,AnonymousWorkspaceService Workspace,ActorAccess Owner,ActorAccess Guest,string Room,string Challenge,string Sid,IConfiguration Config);
    private static async Task<Setup> Create(bool azure = false)
    {
        const string azureTenant = "11111111-1111-4111-8111-111111111111"; var tenant = azure ? azureTenant : "demo";
        var settings = new Dictionary<string,string?> { ["Data:Mode"] = azure ? "Azure" : "Demo",["Ai:Mode"] = "Mock",["Workspace:LegacyApisRestricted"] = "true" };
        for (var i=0;i<3;i++)
        {
            var suffix = $"{i+1:D2}"; settings[$"Auth:Bindings:{i}:TenantId"] = azureTenant; settings[$"Auth:Bindings:{i}:Oid"] = $"33333333-3333-4333-8333-{i+1:D12}";
            settings[$"Auth:Bindings:{i}:PrincipalId"] = "principal-demo-"+suffix; settings[$"Auth:Bindings:{i}:PersonId"] = "demo-person-"+suffix;
            settings[$"Auth:Bindings:{i}:Roles:0"] = "member"; settings[$"Auth:Bindings:{i}:Enabled"] = "true";
            settings[$"Auth:Grants:{i}:PrincipalId"] = "principal-demo-"+suffix; settings[$"Auth:Grants:{i}:ScopeIds:0"] = "demo";
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build(); var identities = new IdentityResolver(config);
        var store = new MemoryRecordStore(); await DemoRecords.SeedAsync(store,tenant,default); var repo = new PrimaryRepository(store);
        for(var i=1;i<=3;i++)
        { var p = (await repo.ReadAsync(RecordPartition.Catalog(tenant),$"demo-person-{i:D2}",default))!; p.Value["ownerId"] = $"principal-demo-{i:D2}"; await repo.CommitAsync(RecordPartition.Catalog(tenant),[RecordWrite.Replace(p.Value,p.ETag)],default); }
        var owner = (azure ? identities.ResolvePerson(tenant,"demo-person-01")! : IdentityResolver.Demo) with { Roles = ["member","publisher"] };
        var guest = azure ? identities.ResolvePerson(tenant,"demo-person-02")! : IdentityResolver.DemoPerson("demo-person-02")!;
        var fence = new AccessFence(repo); var chat = new ConversationService(repo,new(),identities,TimeProvider.System,fence); var ai = new FakeAi();
        var knowledge = new KnowledgeService(repo,store,chat,fence,ai,ai,identities,config,TimeProvider.System);
        var workspace = new AnonymousWorkspaceService(repo,new MemoryProjectionRepository(store),new SourceGuard(repo),fence,chat,knowledge,identities,config,TimeProvider.System);
        var challenge = await workspace.CreateAsync(owner,Key(),new("ソフトの相談","引き継ぎとチェックリストを標準化したい"),default);
        var room = await workspace.ResolveRoomAsync(owner,challenge.RoomId!,default);
        return new(repo,store,fence,chat,workspace,owner,guest,challenge.RoomId!,challenge.Id,room.SessionId,config);
    }
    private static async Task<AnonymousWorkspaceInvitation> Invite(Setup s)
    {
        var session = await s.Chat.SessionAsync(s.Owner,s.Sid,default);
        await s.Chat.InviteAsync(s.Owner,s.Sid,Key(),session.ETag,new(s.Guest.PersonId),s.Owner.TenantId == "demo",default);
        return (await s.Workspace.ConsumeAsync(s.Guest,ct => s.Workspace.InvitationsUnderFenceAsync(s.Guest,100,null,ct),default)).Items.Single();
    }
    private static async Task Join(Setup s)
    { var invitation = await Invite(s); await s.Workspace.DecideAsync(s.Guest,invitation.Id,Key(),invitation.Etag,new("accepted"),default); }
    private static Task<AnonymousWorkspaceKnowledge> State(Setup s,ActorAccess? a = null) => s.Workspace.ConsumeRoomAsync(a ?? s.Owner,s.Room,(r,ct) => s.Workspace.KnowledgeUnderFenceAsync(a ?? s.Owner,r,ct),default);
    private static async Task<AnonymousWorkspaceKnowledge> Change(Setup s,string operation,object input,ActorAccess? a = null)
    {
        var actor = a ?? s.Owner; var state = await State(s,actor);
        await s.Workspace.ChangeKnowledgeAsync(actor,s.Room,operation,Key(),operation == "draft" ? state.SessionEtag : state.Etag,JsonSerializer.SerializeToNode(input,Json)!.AsObject(),default);
        return await State(s,actor);
    }
    private static void PrivateIdentifiersAbsent(Setup s,object value)
    {
        var json = JsonSerializer.Serialize(value,Json);
        foreach(var raw in new[]{s.Sid,s.Owner.PersonId,s.Owner.PrincipalId,s.Guest.PersonId,s.Guest.PrincipalId,"\"tenantId\"","\"ownerId\"","\"participantIds\"","\"sessionId\"","\"sourceRefs\"","\"approvedBy\""}) Assert.DoesNotContain(raw,json);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Existing_identity_bindings_authorize_new_api_without_demo_only_gate(bool azure)
    {
        var s = await Create(azure); var invitation = await Invite(s); Assert.Null(invitation.RoomId); PrivateIdentifiersAbsent(s,invitation);
        await Assert.ThrowsAsync<DomainException>(() => s.Workspace.ConsumeRoomAsync(s.Guest,s.Room,(r,ct) => s.Workspace.MessagesUnderFenceAsync(s.Guest,r,0,10,ct),default));
        var accepted = await s.Workspace.DecideAsync(s.Guest,invitation.Id,Key(),invitation.Etag,new("accepted"),default);
        Assert.Equal(s.Room,accepted.RoomId);
        var ownerRoom = await s.Workspace.ConsumeRoomAsync(s.Owner,s.Room,(r,ct) => s.Workspace.RoomUnderFenceAsync(s.Owner,r,ct),default);
        Assert.Equal(2,ownerRoom.ParticipantCount); PrivateIdentifiersAbsent(s,ownerRoom);
        var own = ownerRoom.Participants.Single(p => p.IsSelf); Assert.StartsWith("awa-",own.AliasId);
        var other = await s.Workspace.ConsumeRoomAsync(s.Guest,s.Room,(r,ct) => s.Workspace.RoomUnderFenceAsync(s.Guest,r,ct),default);
        var left = other.Participation!; await s.Workspace.DecideAsync(s.Guest,left.Id,Key(),left.Etag,new("left"),default);
        await Assert.ThrowsAsync<DomainException>(() => s.Workspace.ConsumeRoomAsync(s.Guest,s.Room,(r,ct) => s.Workspace.RoomUnderFenceAsync(s.Guest,r,ct),default));
    }
    [Fact] public async Task Aliases_are_random_and_unlinkable_across_rooms_and_raw_session_is_rejected()
    {
        var s = await Create(); var first = await s.Workspace.ResolveRoomAsync(s.Owner,s.Room,default);
        var next = await s.Workspace.CreateAsync(s.Owner,Key(),new("別の相談","ソフトを相談する"),default); var second = await s.Workspace.ResolveRoomAsync(s.Owner,next.RoomId!,default);
        Assert.NotEqual(first.Participants.Single().AliasId,second.Participants.Single().AliasId);
        await Assert.ThrowsAsync<DomainException>(() => s.Workspace.ResolveRoomAsync(s.Owner,s.Sid,default));
        await Assert.ThrowsAsync<DomainException>(() => s.Workspace.ResolveRoomAsync(s.Owner with { TenantId = "other" },s.Room,default));
        await Assert.ThrowsAsync<DomainException>(() => s.Workspace.ResolveRoomAsync(s.Owner with { ScopeIds = ["other"] },s.Room,default));
    }
    [Fact] public async Task Client_UUID_is_self_only_and_send_idempotency_conflict_and_stale_version_are_preserved()
    {
        var s = await Create(); await Join(s); var current = await s.Workspace.ResolveRoomAsync(s.Owner,s.Room,default); var key = Key(); var input = new AnonymousWorkspaceSend(Guid.NewGuid().ToString(),"手順を確認した");
        var saved = await s.Workspace.SendAsync(s.Owner,s.Room,key,current.SessionEtag,input,default);
        var replay = await s.Workspace.SendAsync(s.Owner,s.Room,key,current.SessionEtag,input,default); Assert.Equal(saved.Id,replay.Id); Assert.Equal(input.ClientMessageId,saved.ClientMessageId);
        var read = await s.Workspace.ConsumeRoomAsync(s.Guest,s.Room,(r,ct) => s.Workspace.MessagesUnderFenceAsync(s.Guest,r,0,10,ct),default);
        Assert.Null(read.Items.Single().ClientMessageId); PrivateIdentifiersAbsent(s,read);
        var conflict = await Assert.ThrowsAsync<DomainException>(() => s.Workspace.SendAsync(s.Owner,s.Room,key,current.SessionEtag,input with { Body = "変えた" },default)); Assert.Equal("IDEMPOTENCY_CONFLICT",conflict.Code);
        var stale = await Assert.ThrowsAsync<DomainException>(() => s.Workspace.SendAsync(s.Owner,s.Room,Key(),current.SessionEtag,input with { ClientMessageId = Guid.NewGuid().ToString() },default)); Assert.Equal(412,stale.Status);
    }
    [Fact] public async Task Response_consumer_holds_tenant_fence_and_room_lock_until_consumption_completes()
    {
        var s = await Create(); await Join(s); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var read = s.Workspace.ConsumeRoomAsync(s.Guest,s.Room,async (r,ct) => { await s.Workspace.MessagesUnderFenceAsync(s.Guest,r,0,10,ct); entered.SetResult(); await release.Task; return true; },default);
        await entered.Task;
        var error = await Assert.ThrowsAsync<DomainException>(() => s.Workspace.CreateAsync(s.Owner,Key(),new("別の相談","ソフトの相談"),default)); Assert.Equal("ACCESS_BUSY",error.Code);
        var locked = s.Chat.LockedAsync(s.Owner,s.Sid,() => Task.FromResult(true),default); Assert.False(locked.IsCompleted);
        release.SetResult(); await read; await locked;
    }
    [Theory] [InlineData("person@example.com")] [InlineData("demo-person-01")] [InlineData("principal-demo-01")]
    public async Task Known_structured_identity_in_free_input_is_rejected_but_no_complete_text_anonymization_is_claimed(string input)
    { var s = await Create(); var error = await Assert.ThrowsAsync<DomainException>(() => s.Workspace.CreateAsync(s.Owner,Key(),new(input,"相談"),default)); Assert.Equal("IDENTIFYING_CONTENT",error.Code); Assert.False(s.Workspace.Capabilities().FreeTextAnonymized); }
    [Fact] public async Task Candidate_token_is_challenge_and_actor_bound_and_current_availability_is_rechecked()
    {
        var s = await Create(); var candidates = await s.Workspace.ConsumeAsync(s.Owner,ct => s.Workspace.CandidatesUnderFenceAsync(s.Owner,s.Challenge,ct),default); Assert.NotEmpty(candidates.Items); PrivateIdentifiersAbsent(s,candidates);
        var token = candidates.Items[0].Token;
        var row = (await s.Repo.ReadAsync(RecordPartition.Catalog(s.Owner.TenantId),token,default))!; var unit = row.Value["unit"]!.Deserialize<SearchUnit>(Json)!;
        var source = (await s.Repo.ReadAsync(RecordPartition.Catalog(s.Owner.TenantId),unit.EntityId,default))!; source.Value["available"] = false;
        await s.Repo.CommitAsync(RecordPartition.Catalog(s.Owner.TenantId),[RecordWrite.Replace(source.Value,source.ETag)],default);
        var room = await s.Workspace.ResolveRoomAsync(s.Owner,s.Room,default);
        var error = await Assert.ThrowsAsync<DomainException>(() => s.Workspace.InviteAsync(s.Owner,s.Room,Key(),room.SessionEtag,new(token),default)); Assert.Equal("SOURCE_REVOKED",error.Code);
        await Assert.ThrowsAsync<DomainException>(() => s.Workspace.InviteAsync(s.Owner,s.Room,Key(),room.SessionEtag,new(unit.EntityId),default));
    }
    [Fact] public async Task Knowledge_tokens_translate_only_in_own_room_edit_invalidates_consents_and_publication_withdrawal_removes_case()
    {
        var s = await Create(); await Join(s); var room = await s.Workspace.ResolveRoomAsync(s.Owner,s.Room,default);
        await s.Workspace.SendAsync(s.Owner,s.Room,Key(),room.SessionEtag,new(Guid.NewGuid().ToString(),"ソフト 引き継ぎを試した"),default);
        await Change(s,"outcome",new { resolution="partial",result="一部だけ解決" }); var state = await Change(s,"draft",new {});
        PrivateIdentifiersAbsent(s,state); Assert.All(state.ScopeIds,t => Assert.StartsWith("aws-",t)); Assert.All(state.EvidenceIds,t => Assert.StartsWith("awe-",t)); Assert.Contains("あなた",state.ParticipantLabels.Values);
        await Assert.ThrowsAsync<DomainException>(() => Change(s,"edit",new { sections=state.Sections,scopeIds=new[]{"demo"} }));
        state = await Change(s,"edit",new { sections=state.Sections,scopeIds=state.ScopeIds,analysisMetadata=new { departmentId=(string?)null,resolution="partial",reportedHours=1,recurrences=1,feedback=3 } });
        state = await Change(s,"request-review",new { version=state.SourceVersion });
        await Change(s,"consent",new { version=state.SourceVersion,publication=true,analysis=false }); await Change(s,"consent",new { version=state.SourceVersion,publication=true,analysis=false },s.Guest);
        state = await Change(s,"approve",new { version=state.SourceVersion }); Assert.Equal("published",state.Status);
        var partition = RecordPartition.Catalog(s.Owner.TenantId);
        var source = (await s.Repo.ReadAsync(partition,KnowledgeService.Id(s.Sid),default))!;
        var projection = (await s.Store.QueryAsync(partition,"searchUnit","entityId",source.Value.Text("id"),100,null,default)).Items.Single();
        for (var i = 0; i < 100; i++)
        {
            var legacy = source.Value.Copy(); legacy["id"] = "000-legacy-case-"+i.ToString("D3"); legacy["contractVersion"] = "legacy";
            var unit = projection.Value.Copy(); unit["id"] = "000-legacy-projection-"+i.ToString("D3"); unit["entityId"] = legacy["id"]!.DeepClone();
            await s.Store.CommitAsync(partition,[RecordWrite.Create(legacy),RecordWrite.Create(unit)],default);
        }
        IProjectionRepository browse = new MemoryProjectionRepository(s.Store);
        var guarded = await new SourceGuard(s.Repo).FilterAsync(s.Owner,await browse.BrowseKnowledgeAsync(s.Owner.Search,default),default);
        Assert.Equal(100,guarded.Count(u => u.EntityId.StartsWith("000-legacy-case-",StringComparison.Ordinal)));
        var cases = await s.Workspace.ConsumeAsync(s.Owner,ct => s.Workspace.CasesUnderFenceAsync(s.Owner,null,ct),default); var published = Assert.Single(cases.Items); PrivateIdentifiersAbsent(s,published);
        foreach (var query in new[] { "software 改善", "ＳＯＦＴＷＡＲＥ 改善", "ソフトウェア 無関係" })
        {
            var matched = await s.Workspace.ConsumeAsync(s.Owner,ct => s.Workspace.CasesUnderFenceAsync(s.Owner,query,ct),default);
            Assert.Equal(published.Id,Assert.Single(matched.Items).Id);
        }
        Assert.Empty((await s.Workspace.ConsumeAsync(s.Owner,ct => s.Workspace.CasesUnderFenceAsync(s.Owner,"無関係",ct),default)).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => s.Workspace.ConsumeAsync(s.Owner,ct => s.Workspace.CasesUnderFenceAsync(s.Owner,string.Join(' ',Enumerable.Range(0,21).Select(i => "term"+i)),ct),default));
        await Change(s,"edit",new { sections=state.Sections,scopeIds=state.ScopeIds }); var edited = await State(s); Assert.Empty(edited.Consents); Assert.Equal(state.SourceVersion+1,edited.SourceVersion);
        await Assert.ThrowsAsync<DomainException>(() => s.Workspace.ConsumeAsync(s.Owner,ct => s.Workspace.CaseUnderFenceAsync(s.Owner,published.Id,ct),default));
        Assert.Empty((await s.Workspace.ConsumeAsync(s.Owner,ct => s.Workspace.CasesUnderFenceAsync(s.Owner,null,ct),default)).Items);
    }
    [Fact] public async Task Candidate_owner_rebinding_is_rejected_even_when_projection_version_is_unchanged()
    {
        var s = await Create(); var candidates = await s.Workspace.ConsumeAsync(s.Owner,ct => s.Workspace.CandidatesUnderFenceAsync(s.Owner,s.Challenge,ct),default); var token = candidates.Items.First().Token;
        var mapping = (await s.Repo.ReadAsync(RecordPartition.Catalog(s.Owner.TenantId),token,default))!; var unit = mapping.Value["unit"]!.Deserialize<SearchUnit>(Json)!;
        var person = (await s.Repo.ReadAsync(RecordPartition.Catalog(s.Owner.TenantId),unit.EntityId,default))!; person.Value["ownerId"] = "another-principal";
        await s.Repo.CommitAsync(RecordPartition.Catalog(s.Owner.TenantId),[RecordWrite.Replace(person.Value,person.ETag)],default);
        var room = await s.Workspace.ResolveRoomAsync(s.Owner,s.Room,default);
        var error = await Assert.ThrowsAsync<DomainException>(() => s.Workspace.InviteAsync(s.Owner,s.Room,Key(),room.SessionEtag,new(token),default)); Assert.Equal("SOURCE_REVOKED",error.Code);
        var fresh = await s.Workspace.ConsumeAsync(s.Owner,ct => s.Workspace.CandidatesUnderFenceAsync(s.Owner,s.Challenge,ct),default); Assert.DoesNotContain(fresh.Items,c => c.Token == token);
        foreach(var candidate in fresh.Items)
        { var row = (await s.Repo.ReadAsync(RecordPartition.Catalog(s.Owner.TenantId),candidate.Token,default))!; Assert.NotEqual(unit.EntityId,row.Value["unit"]!["entityId"]!.GetValue<string>()); }
    }
    [Fact] public async Task Azure_candidate_checks_current_binding_after_token_issuance()
    {
        var s = await Create(true); var candidates = await s.Workspace.ConsumeAsync(s.Owner,ct => s.Workspace.CandidatesUnderFenceAsync(s.Owner,s.Challenge,ct),default); var token = candidates.Items.First().Token;
        var mapping = (await s.Repo.ReadAsync(RecordPartition.Catalog(s.Owner.TenantId),token,default))!; var unit = mapping.Value["unit"]!.Deserialize<SearchUnit>(Json)!;
        var bindingIndex = int.Parse(unit.EntityId[^2..])-1; s.Config[$"Auth:Bindings:{bindingIndex}:Enabled"] = "false";
        var room = await s.Workspace.ResolveRoomAsync(s.Owner,s.Room,default);
        var error = await Assert.ThrowsAsync<DomainException>(() => s.Workspace.InviteAsync(s.Owner,s.Room,Key(),room.SessionEtag,new(token),default)); Assert.Equal("SOURCE_REVOKED",error.Code);
    }
    [Fact] public async Task Requester_owned_participation_uses_private_recipient_binding_and_recipient_joins_without_requester_approval()
    {
        var s = await Create(); var invitation = await Invite(s);
        var stored = (await s.Chat.ParticipationsAsync(s.Guest,s.Sid,default)).Items.Single(); Assert.Equal(s.Owner.PrincipalId,stored.Value.Text("ownerId"));
        var reference = (await s.Repo.ReadAsync(RecordPartition.Catalog(s.Owner.TenantId),invitation.Id,default))!;
        Assert.Equal(s.Guest.PrincipalId,reference.Value.Text("recipientPrincipalId"));
        var accepted = await s.Workspace.DecideAsync(s.Guest,invitation.Id,Key(),invitation.Etag,new("accepted"),default); Assert.Equal(s.Room,accepted.RoomId);
        var current = await s.Workspace.ResolveRoomAsync(s.Guest,s.Room,default); Assert.Equal(2,current.Participants.Length);
    }
    [Theory] [InlineData("declined")] [InlineData("left")]
    public async Task Decline_and_leave_lost_ack_replay_validates_original_receipt_without_restoring_room_access(string decision)
    {
        var s = await Create(); var invitation = await Invite(s);
        if (decision == "left")
        {
            await s.Workspace.DecideAsync(s.Guest,invitation.Id,Key(),invitation.Etag,new("accepted"),default);
            invitation = await s.Workspace.ConsumeAsync(s.Guest,ct => s.Workspace.InvitationUnderFenceAsync(s.Guest,invitation.Id,ct),default);
        }
        var key = Key(); var saved = await s.Workspace.DecideAsync(s.Guest,invitation.Id,key,invitation.Etag,new(decision),default);
        var replay = await s.Workspace.DecideAsync(s.Guest,invitation.Id,key,invitation.Etag,new(decision),default); Assert.Equal(saved,replay); Assert.Null(replay.RoomId);
        var conflict = await Assert.ThrowsAsync<DomainException>(() => s.Workspace.DecideAsync(s.Guest,invitation.Id,key,invitation.Etag,new("accepted"),default)); Assert.Equal(409,conflict.Status); Assert.Equal("IDEMPOTENCY_CONFLICT",conflict.Code);
        var newKey = await Assert.ThrowsAsync<DomainException>(() => s.Workspace.DecideAsync(s.Guest,invitation.Id,Key(),invitation.Etag,new(decision),default)); Assert.Equal(409,newKey.Status);
        await Assert.ThrowsAsync<DomainException>(() => s.Workspace.ConsumeRoomAsync(s.Guest,s.Room,(r,ct) => s.Workspace.MessagesUnderFenceAsync(s.Guest,r,0,10,ct),default));
    }
    [Theory]
    [InlineData(false,"rebind")] [InlineData(true,"rebind")]
    [InlineData(false,"disabled")] [InlineData(true,"disabled")]
    [InlineData(false,"profile")] [InlineData(true,"profile")]
    [InlineData(false,"scope")] [InlineData(true,"scope")]
    public async Task Invitation_reads_recheck_current_recipient_and_original_principal(bool legacy,string change)
    {
        var s = await Create(true); var invitation = await Invite(s); var partition = RecordPartition.Catalog(s.Owner.TenantId);
        if (legacy)
        {
            var reference = (await s.Repo.ReadAsync(partition,invitation.Id,default))!;
            reference.Value.Remove("recipientPrincipalId");
            await s.Repo.CommitAsync(partition,[RecordWrite.Replace(reference.Value,reference.ETag)],default);
        }
        var valid = await s.Workspace.ConsumeAsync(s.Guest,ct => s.Workspace.InvitationUnderFenceAsync(s.Guest,invitation.Id,ct),default);
        Assert.Equal(invitation.Title,valid.Title); PrivateIdentifiersAbsent(s,valid);
        var actor = s.Guest;
        if (change is "profile" or "rebind")
        {
            var profile = (await s.Repo.ReadAsync(partition,s.Guest.PersonId,default))!; profile.Value["ownerId"] = "replacement-principal";
            await s.Repo.CommitAsync(partition,[RecordWrite.Replace(profile.Value,profile.ETag)],default);
        }
        if (change == "rebind")
        {
            s.Config["Auth:Bindings:1:PrincipalId"] = "replacement-principal";
            s.Config["Auth:Grants:1:PrincipalId"] = "replacement-principal";
            actor = new IdentityResolver(s.Config).ResolvePerson(s.Owner.TenantId,s.Guest.PersonId)!;
        }
        if (change == "disabled") s.Config["Auth:Bindings:1:Enabled"] = "false";
        if (change == "scope") actor = actor with { ScopeIds = ["outside"] };
        var opened = await Assert.ThrowsAsync<DomainException>(() => s.Workspace.ConsumeAsync(actor,ct => s.Workspace.InvitationUnderFenceAsync(actor,invitation.Id,ct),default));
        Assert.Equal(404,opened.Status);
        // The list may omit an out-of-scope challenge before reaching its DTO.
        if (change == "scope") Assert.Empty((await s.Workspace.ConsumeAsync(actor,ct => s.Workspace.InvitationsUnderFenceAsync(actor,100,null,ct),default)).Items);
        else
        {
            var listed = await Assert.ThrowsAsync<DomainException>(() => s.Workspace.ConsumeAsync(actor,ct => s.Workspace.InvitationsUnderFenceAsync(actor,100,null,ct),default));
            Assert.Equal(404,listed.Status);
        }
    }
    [Fact] public async Task Status_only_replay_rechecks_current_binding_and_scope_after_departure()
    {
        var s = await Create(true); var invitation = await Invite(s); var key = Key();
        await s.Workspace.DecideAsync(s.Guest,invitation.Id,key,invitation.Etag,new("declined"),default);
        await Assert.ThrowsAsync<DomainException>(() => s.Workspace.DecideAsync(s.Guest with { ScopeIds=["outside"] },invitation.Id,key,invitation.Etag,new("declined"),default));
        s.Config["Auth:Bindings:1:Enabled"] = "false";
        await Assert.ThrowsAsync<DomainException>(() => s.Workspace.DecideAsync(s.Guest,invitation.Id,key,invitation.Etag,new("declined"),default));
    }
    [Fact] public void Unknown_identity_properties_are_not_deserializable_into_public_inputs()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AnonymousWorkspaceInvite>("{\"candidateToken\":\"awt-x\",\"personId\":\"employee\"}",Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AnonymousWorkspaceSend>("{\"clientMessageId\":\"x\",\"body\":\"x\",\"ownerId\":\"employee\"}",Json));
    }
}
