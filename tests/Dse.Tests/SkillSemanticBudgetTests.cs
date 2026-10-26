using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public sealed class SkillSemanticBudgetTests
{
    private static readonly ActorAccess A=SkillDictionaryFixture.Curator;
    private static readonly JsonSerializerOptions J=new(JsonSerializerDefaults.Web);
    private static JsonNode N<T>(T value)=>JsonSerializer.SerializeToNode(value,J)!;
    private static string Key()=>Guid.NewGuid().ToString("N");
    // Boundary/encoding fixtures generated in memory, not an evaluation dataset.
    private static JsonArray Cases()
    {
        var rows=new JsonArray();
        for(var i=0;i<3;i++)
        {
            var dictionary=new JsonArray();
            for(var j=0;j<6;j++)dictionary.Add(new JsonObject{["id"]="budget-skill-"+j,["name"]=j==0?"回帰試験":"別の操作"+j,["definition"]="仕様に対する確認。"+new string('試',100),["boundary"]="他者・知識・資格の経験を補完しない"});
            rows.Add(new JsonObject{["case"]=new JsonObject{["id"]="budget-"+i,["raw"]="リグレッション試験",["source"]="私はリグレッション試験を実施した。"+new string('文',i*8),["dictionary"]=dictionary},["selectedIds"]=N(new[]{"budget-skill-0","budget-skill-1","budget-skill-2"})});
        }
        return rows;
    }
    private static async Task<(SkillDictionaryFixture F,StoredRecord C,JsonObject D)> Setup(JsonNode row,string? source=null)
    {
        var c=row["case"]!;var f=await SkillDictionaryFixture.Create();var id=c["id"]!.GetValue<string>();
        await f.Source(id,c["raw"]!.GetValue<string>(),source??c["source"]!.GetValue<string>());
        var concepts=c["dictionary"]!.AsArray().Select(x=>new SkillConcept(x!["id"]!.GetValue<string>(),x["name"]!.GetValue<string>(),x["definition"]!.GetValue<string>(),x["boundary"]!.GetValue<string>(),["demo"],"active",null,1,[new(x["name"]!.GetValue<string>(),"practice",null,[])])).ToArray();
        var p=RecordPartition.Catalog("demo");var v=RecordJson.New("skillDictionaryVersion","skill-dictionary-version-1","demo",A.PrincipalId,["demo"],DateTimeOffset.UtcNow);v["snapshot"]=N(new SkillDictionarySnapshot(1,concepts));
        var head=RecordJson.New("skillDictionaryHead",SkillDictionaryService.HeadId,"demo",A.PrincipalId,["demo"],DateTimeOffset.UtcNow);head["version"]=1;
        await f.Store.CommitAsync(p,[RecordWrite.Create(v),RecordWrite.Create(head)],default);
        var collect=await f.Service.CollectAsync(A,Key(),new("fragment-"+id),default);
        var candidate=(await f.Repo.ReadAsync(p,collect.Body!["bindings"]![0]!["candidateId"]!.GetValue<string>(),default))!;
        return(f,candidate,await f.Fence.RunAsync("demo",ct=>f.Service.ReadUnderFenceAsync(A,null,ct),default));
    }
    private static Task<JsonObject> Suggest(SkillDictionaryService s,StoredRecord c,JsonObject d,string? key=null)=>s.SuggestAsync(A,c.Value.Text("id"),key??Key(),c.ETag,new(d.Number("version"),d.Text("etag")),default);
    private static JsonObject OldBody(JsonNode row,StoredRecord c,JsonObject d)
    {
        var snapshot=new SkillDictionarySnapshot(1,d["concepts"]!.Deserialize<SkillConcept[]>(J)!);
        var e=c.Value["element"]!.Deserialize<SkillElement>(J)!;var item=c.Value["item"]!.Deserialize<SpecificSkill>(J)!;
        var selected=SkillSemanticMatching.Select(SkillSemanticMatching.Applicable(snapshot,e,["demo"]),e);
        Assert.Equal(row["selectedIds"]!.Deserialize<string[]>(J),selected.Select(x=>x.Id));
        var qs=new JsonObject();foreach(var q in SkillSemanticMatching.Questions(selected.Length))qs[q.Id]=new JsonObject{["type"]="noul",["instructions"]=q.Instructions};
        var id=row["case"]!["id"]!.GetValue<string>();
        return new(){["model"]="jev-1.13.0",["state"]=new JsonObject{["text"]=SkillSemanticMatching.Input(item,e,selected),["evidence"]=new JsonArray(new JsonObject{["id"]="ev-"+id,["documentVersionId"]="ver-"+id,["sourceVersion"]=1,["text"]=row["case"]!["source"]!.DeepClone()}),["candidates"]=new JsonArray()},["questions"]=qs};
    }
    private static JsonObject ChoiceBody(StoredRecord candidate,JsonObject dictionary)
    {
        var item=candidate.Value["item"]!.Deserialize<SpecificSkill>(J)!;var element=candidate.Value["element"]!.Deserialize<SkillElement>(J)!;
        var all=SkillChoiceMatching.Applicable(new(1,dictionary["concepts"]!.Deserialize<SkillConcept[]>(J)!),["demo"],["demo"]);var q=SkillChoiceMatching.Question(all);
        var input=new JsonObject{["text"]=SkillChoiceMatching.Input(item,element,all),["evidence"]=new JsonArray(new JsonObject{["id"]=item.EvidenceId,["documentVersionId"]="ver-"+item.EvidenceId[3..],["sourceVersion"]=1,["text"]=item.Quote}),["candidates"]=new JsonArray()};
        return new(){["model"]="jev-1.13.0",["state"]=SkillChoiceWire.Prepare(SkillSemanticBudget.Prepare(input,[new(item.EvidenceId,"ver-"+item.EvidenceId[3..],1)]),q),["questions"]=new JsonObject{[q.Id]=new JsonObject{["type"]="choice",["instructions"]=q.Instructions,["criteria"]=N(SkillChoiceWire.Options(q))}}};
    }
    [Fact]
    public async Task Generated_large_inputs_keep_lossless_compaction_and_new_choice_sends_all_concepts_once()
    {
        var measurements=new JsonArray();foreach(var row in Cases())
        {
            var(f,c,d)=await Setup(row!);var old=OldBody(row!,c,d);var before=Encoding.UTF8.GetByteCount(old.ToJsonString())+256;
            Assert.True(before>8000);
            var originalInput=old["state"]!.AsObject();old["state"]=SkillSemanticBudget.Prepare(originalInput,[new("ev-"+row!["case"]!["id"]!.GetValue<string>(),"ver-"+row["case"]!["id"]!.GetValue<string>(),1)]);
            var legacyCompact=Encoding.UTF8.GetByteCount(SkillSemanticBudget.Serialize(old))+256;Assert.True(legacyCompact<8000);
            var config=SkillSemanticTestRuntime.Config();config["AiUsage:MaxInputTokens"]="8000";var wire=new SkillSemanticTestRuntime.Wire();using var ai=SkillSemanticTestRuntime.Ai(f,config,wire);var service=new SkillDictionaryService(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>ai);
            var key=Key();var result=await Suggest(service,c,d,key);Assert.NotEqual("AI_INPUTLIMIT",result.Text("reason"));Assert.Equal(1,wire.Calls);
            var body=ChoiceBody(c,d);Assert.True(JsonNode.DeepEquals(body,wire.Last));Assert.Single(wire.Last!["questions"]!.AsObject());Assert.Equal(d["concepts"]!.AsArray().Count+2,wire.Last["questions"]![SkillChoiceMatching.QuestionId]!["criteria"]!.AsObject().Count);
            await Suggest(service,c,d,key);await Suggest(service,c,d);Assert.Equal(1,wire.Calls);
            var after=Encoding.UTF8.GetByteCount(SkillSemanticBudget.Serialize(wire.Last))+256;Assert.InRange(after,1,8000);var run=(await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),result.Text("aiRunId"),default))!.Value;Assert.Equal(after,run["trace"]!["inputByteBound"]!.GetValue<int>());
            measurements.Add(new JsonObject{["id"]=row["case"]!["id"]!.DeepClone(),["originalNoulByteBound"]=before,["losslessNoulByteBound"]=legacyCompact,["choiceByteBound"]=after,["externalApiCalls"]=0});
        }
        var output=Environment.GetEnvironmentVariable("SKILL_INPUT_BUDGET_RESULT");if(output is not null)await File.WriteAllTextAsync(output,measurements.ToJsonString(new(){WriteIndented=true}));
    }
    [Theory]
    [InlineData(0,true)][InlineData(-1,false)][InlineData(1,true)]
    public async Task Exact_choice_byte_boundary_uses_the_same_transport_serializer(int offset,bool allowed)
    {
        var(f,c,d)=await Setup(Cases()[0]!);var body=ChoiceBody(c,d);var bound=Encoding.UTF8.GetByteCount(SkillSemanticBudget.Serialize(body))+256;
        var config=SkillSemanticTestRuntime.Config();config["AiUsage:MaxInputTokens"]=(bound+offset).ToString();var wire=new SkillSemanticTestRuntime.Wire();using var ai=SkillSemanticTestRuntime.Ai(f,config,wire);var service=new SkillDictionaryService(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>ai);
        var result=await Suggest(service,c,d);Assert.Equal(allowed?1:0,wire.Calls);if(!allowed){Assert.Equal("AI_INPUTLIMIT",result.Text("reason"));Assert.True(result["inferenceNotDispatched"]!.GetValue<bool>());Assert.False(result["providerExecutionUncertain"]!.GetValue<bool>());await Suggest(service,c,d);Assert.Equal(0,wire.Calls);}
    }
    [Fact]
    public void Partial_quote_keeps_full_source_tail_and_all_conditions_roles_and_dictionary_fields()
    {
        var quote="私は試験を補助した。";var suffix="通常拠点は使用せず、本人が設計したわけではない。";
        var input=new JsonObject{["text"]=SkillSemanticMatching.Input(new("試験",null,"ev",quote,"assisted"),new("試験","補助","装置",["高温","通常拠点を使用しない"],"practice","person","assisted",null,"unmapped",[]),[]),["evidence"]=new JsonArray(new JsonObject{["id"]="ev",["text"]=quote+new string('あ',4000)+suffix}),["candidates"]=new JsonArray()};
        var packed=SkillSemanticBudget.Prepare(input,[new("ev","ver",1)]);Assert.Equal(quote,packed["text"]!["candidate"]!["quote"]!.GetValue<string>());
        var sent=SkillSemanticBudget.Serialize(new(){["state"]=packed});Assert.Contains(suffix,sent);Assert.Contains("高温",sent);Assert.Contains("assisted",sent);Assert.Contains("装置",sent);
        Assert.True(JsonNode.DeepEquals(input["evidence"],packed["evidence"]));Assert.IsAssignableFrom<JsonValue>(input["text"]!);
    }
    [Fact]
    public async Task Long_evidence_is_rejected_whole_instead_of_dropping_trailing_negation()
    {
        var row=Cases()[0]!;var source=row["case"]!["source"]!.GetValue<string>()+new string('あ',3000)+"通常拠点を使用しない。本人の設計経験ではない。";
        var(f,c,d)=await Setup(row,source);var config=SkillSemanticTestRuntime.Config();config["AiUsage:MaxInputTokens"]="8000";var wire=new SkillSemanticTestRuntime.Wire();using var ai=SkillSemanticTestRuntime.Ai(f,config,wire);var s=new SkillDictionaryService(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>ai);
        var r=await Suggest(s,c,d);Assert.Equal("AI_INPUTLIMIT",r.Text("reason"));Assert.Equal("requires_review",r.Text("outcome"));Assert.Equal(0,wire.Calls);Assert.False(r["providerExecutionUncertain"]!.GetValue<bool>());
    }
    [Theory]
    [InlineData("AI_INPUTLIMIT","complete",false,true)]
    [InlineData("AI_UNAVAILABLE","complete",true,false)]
    [InlineData("DISPATCHED_AWAITING_RESULT","dispatching",false,false)]
    [InlineData("NOT_EVALUATED","prepared",false,false)]
    [InlineData("SEMANTIC_PROPOSAL_ONLY","complete",false,false)]
    public async Task Old_noul_history_is_visible_but_never_reused_for_explicit_new_choice_method(string reason,string phase,bool uncertain,bool maySend)
    {
        var row=Cases()[0]!;var(f,c,d)=await Setup(row);var p=RecordPartition.Catalog("demo");
        var snapshot=new SkillDictionarySnapshot(1,d["concepts"]!.Deserialize<SkillConcept[]>(J)!);
        string H(string text)=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var hash=H(RecordJson.Canonical(N(snapshot)));
        var id="skill-proposal-"+H(A.PrincipalId+":"+c.Value.Text("id")+":"+c.Value.Text("sourceFingerprint")+":"+hash+":"+SkillSemanticMatching.Version);
        var legacy=RecordJson.New("skillMatchProposal",id,"demo",A.PrincipalId,["demo"],DateTimeOffset.UtcNow);
        legacy["candidateId"]=c.Value.Text("id");legacy["sourceFingerprint"]=c.Value.Text("sourceFingerprint");legacy["dictionaryVersion"]=1;legacy["snapshotHash"]=hash;legacy["policyVersion"]=SkillSemanticMatching.Version;
        legacy["phase"]=phase;legacy["outcome"]="requires_review";legacy["reason"]=reason;legacy["comparisons"]=new JsonArray();legacy["basis"]="semantic_noul";legacy["shortlistComplete"]=false;legacy["applicableCount"]=snapshot.Concepts.Length;legacy["targetIds"]=row["selectedIds"]!.DeepClone();legacy["aiRunId"]=null;legacy["providerExecutionUncertain"]=uncertain;
        await f.Store.CommitAsync(p,[RecordWrite.Create(legacy)],default);
        var reusedKey=Key();await f.Fence.RunAsync("demo",ct=>new IdempotencyExecutor(f.Repo,TimeProvider.System).ExecuteAsync(p,A,"POST","skill-candidates/"+c.Value.Text("id")+"/suggest",reusedKey,c.ETag,N(new SkillSuggestionRequest(1,d.Text("etag"))),()=>Task.FromResult(new MutationPlan([],null,new(200,new JsonObject{["proposalId"]=id},null,null))),ct),default);
        var saved=await f.Fence.RunAsync("demo",ct=>f.Service.ReadSuggestionUnderFenceAsync(A,c.Value.Text("id"),1,ct),default);Assert.Equal("not_evaluated",saved.Text("phase"));var previous=Assert.Single(saved["previousProposals"]!.AsArray())!;Assert.Equal(id,previous["proposalId"]!.GetValue<string>());Assert.Equal("legacy-json-string-v1",previous["inputVersion"]!.GetValue<string>());
        var wire=new SkillSemanticTestRuntime.Wire();using var ai=SkillSemanticTestRuntime.Ai(f,SkillSemanticTestRuntime.Config(),wire);var s=new SkillDictionaryService(f.Repo,f.Fence,new(f.Repo,TimeProvider.System),TimeProvider.System,()=>ai);
        var r=await Suggest(s,c,d,reusedKey);Assert.Equal(1,wire.Calls);Assert.NotEqual(id,r.Text("proposalId"));Assert.Equal("choice",r.Text("method"));Assert.Single(r["previousProposals"]!.AsArray());_ = maySend;
        Assert.Equal(reason,(await f.Repo.ReadAsync(p,id,default))!.Value.Text("reason"));await Suggest(s,c,d);Assert.Equal(1,wire.Calls);
    }
}
