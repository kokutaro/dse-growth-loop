using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

// Local service tests only. Fake decisions do not validate Jev semantic quality.
public sealed class SkillSemanticServiceTests
{
    private static readonly ActorAccess A=SkillDictionaryFixture.Curator;
    private static string Key()=>Guid.NewGuid().ToString("N");
    private sealed class Judge : IDecisionProvider
    {
        public int Calls;public AiFailure? Failure;public Func<Task>? During;public double Forward=.92,Reverse=.93;
        public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release;
        public async Task<AiResult<Decisions>> DecideAsync(AiContext context,string text,ChoiceQuestion[] choices,NoulQuestion[] nouls,CancellationToken ct)
        {
            Calls++;Started.TrySetResult();if(Release is not null)await Release.Task.WaitAsync(ct);if(During is not null)await During();
            Assert.Equal(SkillChoiceMatching.Version,context.PromptVersion);Assert.Single(choices);Assert.Empty(nouls);Assert.NotEmpty(context.Evidence);
            return new(Failure is null?AiStatus.Succeeded:AiStatus.Failed,Failure is null?new(new Dictionary<string,ChoiceAnswer>{{choices[0].Id,new(choices[0].Options.Keys.First(k=>k is not (SkillChoiceMatching.None or SkillChoiceMatching.Uncertain)),choices[0].Options.Keys.ToDictionary(k=>k,k=>k==choices[0].Options.Keys.First()?1d:0d),1)}},new Dictionary<string,NoulAnswer>()):null,"mock-semantic-run",Failure);
        }
    }
    private static async Task<JsonObject> Dict(SkillDictionaryFixture f)=>await f.Fence.RunAsync("demo",ct=>f.Service.ReadUnderFenceAsync(A,null,ct),default);
    private static async Task<StoredRecord> Collect(SkillDictionaryFixture f,string name)
    {
        var r=await f.Service.CollectAsync(A,Key(),new("fragment-"+name),default);var id=r.Body!["bindings"]![0]!["candidateId"]!.GetValue<string>();return (await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),id,default))!;
    }
    private static async Task<string> Register(SkillDictionaryFixture f,string name)
    {
        var c=await Collect(f,name);var d=await Dict(f);var r=await f.Service.ReviewAsync(A,c.Value.Text("id"),Key(),c.ETag,new("new","固定した仮データの技能定義を確認",d.Number("version"),d.Text("etag"),Name:c.Value["element"]!["label"]!.GetValue<string>(),Definition:"対象の変更後も既存機能が動くことを確認",Boundary:"性能計測、知識、資格、他者経験は同一視しない"),default);return r.Body!["conceptId"]!.GetValue<string>();
    }
    private static async Task<(SkillDictionaryFixture F,Judge J,StoredRecord C,JsonObject D,string Target)> Setup(bool register=true)
    {
        var f=await SkillDictionaryFixture.Create();var j=new Judge();f=f with{Service=new(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>j)};
        var target=register?await Register(f,"regression"):"";var c=await Collect(f,"alias");return(f,j,c,await Dict(f),target);
    }
    private static Task<JsonObject> Suggest(SkillDictionaryFixture f,StoredRecord c,JsonObject d,string? key=null,ActorAccess? actor=null)=>
        f.Service.SuggestAsync(actor??A,c.Value.Text("id"),key??Key(),c.ETag,new(d.Number("version"),d.Text("etag")),default);
    [Fact]
    public async Task Semantic_proposal_never_approves_alias_changes_gold_or_publishes_experience()
    {
        var (f,j,c,d,target)=await Setup();var p=RecordPartition.Catalog("demo");var source=RecordJson.Canonical((await f.Repo.ReadAsync(p,"fragment-alias",default))!.Value);var dictionary=RecordJson.Canonical((await f.Repo.ReadAsync(p,"skill-dictionary-version-1",default))!.Value);
        var result=await Suggest(f,c,d);Assert.Equal("existing_candidate",result.Text("outcome"));Assert.Equal(target,result["choice"]!["selectedId"]!.GetValue<string>());Assert.Equal(1,j.Calls);
        Assert.True(result["requiresHumanApproval"]!.GetValue<bool>());Assert.False(result["experienceChanged"]!.GetValue<bool>());Assert.False(result["goldChanged"]!.GetValue<bool>());
        Assert.Equal(source,RecordJson.Canonical((await f.Repo.ReadAsync(p,"fragment-alias",default))!.Value));Assert.Equal(dictionary,RecordJson.Canonical((await f.Repo.ReadAsync(p,"skill-dictionary-version-1",default))!.Value));
        Assert.Equal("pending",(await f.Repo.ReadAsync(p,c.Value.Text("id"),default))!.Value.Text("state"));Assert.Single((await f.Repo.QueryAsync(p,"skillDictionaryReview",null,null,100,null,default)).Items);
        var record=Assert.Single((await f.Repo.QueryAsync(p,"skillMatchProposal",null,null,100,null,default)).Items).Value;
        var directory=Path.Combine(AppContext.BaseDirectory,"../../../TestResults");Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory,"skill-semantic-contract.json"),new JsonObject{["schema"]="SkillMatchProposal",["value"]=record.DeepClone()}.ToJsonString());
    }
    [Fact]
    public async Task Same_or_new_keys_reuse_durable_proposal_and_unknown_provider_result_never_resends()
    {
        var (f,j,c,d,_)=await Setup();j.Failure=AiFailure.Timeout;var key=Key();var first=await Suggest(f,c,d,key);var same=await Suggest(f,c,d,key);var other=await Suggest(f,c,d);
        Assert.Equal("requires_review",first.Text("outcome"));Assert.Equal("AI_TIMEOUT",first.Text("reason"));Assert.True(first["providerExecutionUncertain"]!.GetValue<bool>());Assert.Equal(1,j.Calls);Assert.True(JsonNode.DeepEquals(first,same));Assert.True(JsonNode.DeepEquals(first,other));
    }
    [Fact]
    public async Task Concurrent_request_observes_dispatching_intent_without_a_second_paid_attempt()
    {
        var (f,j,c,d,_)=await Setup();j.Release=new(TaskCreationOptions.RunContinuationsAsynchronously);var first=Suggest(f,c,d);await j.Started.Task;
        var second=await Suggest(f,c,d);Assert.Equal("dispatching",second.Text("phase"));Assert.Equal(1,j.Calls);j.Release.TrySetResult();Assert.Equal("existing_candidate",(await first).Text("outcome"));
    }
    [Fact]
    public async Task Empty_or_kind_incompatible_dictionary_is_only_a_new_candidate_with_no_model_call()
    {
        var (f,j,c,d,_)=await Setup(false);var result=await Suggest(f,c,d);Assert.Equal("new_candidate",result.Text("outcome"));Assert.Equal("empty_scoped_catalogue",result.Text("basis"));Assert.Equal(0,j.Calls);
        await Register(f,"regression");var knowledge=await Collect(f,"knowledge");var result2=await Suggest(f,knowledge,await Dict(f));Assert.Equal("requires_review",result2.Text("outcome"));Assert.Equal("CHOICE_KIND_MISMATCH",result2.Text("reason"));Assert.Equal(1,j.Calls);
        Assert.Equal("content_only",knowledge.Value["item"]!["analysis"]!["experienceState"]!.GetValue<string>());
    }
    [Fact]
    public async Task Dictionary_change_during_model_execution_requires_reconfirmation_at_the_original_version()
    {
        var (f,j,c,d,_)=await Setup();j.During=async()=>{await Register(f,"performance");};var result=await Suggest(f,c,d);
        Assert.Equal("requires_review",result.Text("outcome"));Assert.Equal("DICTIONARY_CHANGED",result.Text("reason"));Assert.Equal(1,result.Number("dictionaryVersion"));Assert.True(result["stale"]!.GetValue<bool>());Assert.Equal(2,(await Dict(f)).Number("version"));
    }
    [Fact]
    public async Task Unauthorized_member_and_other_scope_do_not_send_or_reveal_ids()
    {
        var (f,j,c,d,_)=await Setup();foreach(var actor in new[]{SkillDictionaryFixture.Member,SkillDictionaryFixture.OtherScope})
        {var e=await Assert.ThrowsAsync<DomainException>(()=>Suggest(f,c,d,actor:actor));Assert.Contains(e.Status,new[]{403,404});}Assert.Equal(0,j.Calls);
    }
    [Fact]
    public async Task Revoked_source_after_model_does_not_reveal_or_repeat_a_proposal()
    {
        var (f,j,c,d,_)=await Setup();j.During=async()=>{var p=RecordPartition.Catalog("demo");var doc=(await f.Repo.ReadAsync(p,"doc-alias",default))!;doc.Value["accessVersion"]=2;await f.Store.CommitAsync(p,[RecordWrite.Replace(doc.Value,doc.ETag)],default);};
        await Assert.ThrowsAsync<DomainException>(()=>Suggest(f,c,d));await Assert.ThrowsAsync<DomainException>(()=>Suggest(f,c,d));Assert.Equal(1,j.Calls);
        var rows=await f.Repo.QueryAsync(RecordPartition.Catalog("demo"),"skillMatchProposal",null,null,100,null,default);Assert.Equal("requires_review",Assert.Single(rows.Items).Value.Text("outcome"));
    }
    [Fact]
    public async Task Stored_foreign_concept_id_is_rejected_before_cache_read_or_dispatch()
    {
        var (f,j,c,d,_)=await Setup();await Suggest(f,c,d);var p=RecordPartition.Catalog("demo");var row=Assert.Single((await f.Repo.QueryAsync(p,"skillMatchProposal",null,null,100,null,default)).Items);row.Value["targetIds"]=RecordJson.Array(["invented-foreign-id"]);await f.Store.CommitAsync(p,[RecordWrite.Replace(row.Value,row.ETag)],default);
        var e=await Assert.ThrowsAsync<DomainException>(()=>f.Fence.RunAsync("demo",ct=>f.Service.ReadSuggestionUnderFenceAsync(A,c.Value.Text("id"),1,ct),default));Assert.Equal("SKILL_PROPOSAL_INVALID",e.Code);Assert.Equal(1,j.Calls);
    }

    [Fact]
    public async Task Previous_choice_version_is_read_only_history_not_a_noul_score()
    {
        var(f,j,c,d,_)=await Setup();await Suggest(f,c,d);var p=RecordPartition.Catalog("demo");var current=Assert.Single((await f.Repo.QueryAsync(p,"skillMatchProposal",null,null,100,null,default)).Items).Value;
        var previous=current.Copy();previous["policyVersion"]="skill-catalogue-choice-v1";previous["id"]="skill-proposal-"+ConversationService.Hash(A.PrincipalId+":"+c.Value.Text("id")+":"+current.Text("sourceFingerprint")+":"+current.Text("snapshotHash")+":skill-catalogue-choice-v1:"+SkillSemanticBudget.Version+":"+SkillChoiceWire.Version+":"+ChoiceProbabilityRules.Version+":"+current.Text("candidateSetHash"));
        await f.Store.CommitAsync(p,[RecordWrite.Create(previous)],default);
        var view=await f.Fence.RunAsync("demo",t=>f.Service.ReadSuggestionUnderFenceAsync(A,c.Value.Text("id"),d.Number("version"),t),default);
        var old=Assert.Single(view["previousProposals"]!.AsArray())!;Assert.Equal("choice",old["method"]!.GetValue<string>());Assert.Null(old["threshold"]);Assert.NotNull(old["choice"]);Assert.Equal(1,j.Calls);
    }
}
