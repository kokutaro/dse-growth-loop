using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed partial class SkillDictionaryService
{
    private static EvidenceReference[] References(JsonObject f)=>f.Text("kind")=="knowledge"
        ?[new(f.Text("id"),f.Text("id"),f.Number("activeVersion"))]
        :f["evidenceRefs"]?.Deserialize<EvidenceReference[]>(ProjectionRules.Json)??[];
    private static string SnapshotHash(SkillDictionarySnapshot s)=>Hash(RecordJson.Canonical(Node(s)));
    private static string CandidateSetHash(ActorAccess a,JsonObject c,SkillDictionarySnapshot s)=>Hash(RecordJson.Canonical(Node(SkillChoiceMatching.Applicable(s,c.Strings("scopeIds"),a.ScopeIds).Select(x=>x.Id).ToArray())));
    private static string SuggestionId(ActorAccess a,JsonObject c,SkillDictionarySnapshot s)=>"skill-proposal-"+Hash(a.PrincipalId+":"+c.Text("id")+":"+c.Text("sourceFingerprint")+":"+SnapshotHash(s)+":"+SkillChoiceMatching.Version+":"+SkillSemanticBudget.Version+":"+SkillChoiceWire.Version+":"+ChoiceProbabilityRules.Version+":"+CandidateSetHash(a,c,s));
    private static string NoulSuggestionId(ActorAccess a,JsonObject c,SkillDictionarySnapshot s,bool legacy)=>"skill-proposal-"+Hash(a.PrincipalId+":"+c.Text("id")+":"+c.Text("sourceFingerprint")+":"+SnapshotHash(s)+":"+SkillSemanticMatching.Version+(legacy?"":":"+SkillSemanticBudget.Version));
    private static void CompleteProposal(JsonObject p,SkillSuggestionDecision decision,string? aiRun=null,bool uncertain=false)
    {
        p["phase"]="complete";p["outcome"]=decision.Outcome;p["reason"]=decision.Reason;p["comparisons"]=Node(decision.Comparisons);p["aiRunId"]=aiRun;p["providerExecutionUncertain"]=uncertain;
    }
    private static JsonObject View(JsonObject p,SkillDictionarySnapshot snapshot,bool stale)
    {
        var choice=p.Text("policyVersion")==SkillChoiceMatching.Version;
        return new(){
            ["proposalId"]=p["id"]!.DeepClone(),["candidateId"]=p["candidateId"]!.DeepClone(),["dictionaryVersion"]=snapshot.Version,
            ["method"]=choice?"choice":"noul",["wireVersion"]=p["wireVersion"]?.DeepClone(),["probabilityPolicyVersion"]=p["probabilityPolicyVersion"]?.DeepClone(),["policyVersion"]=p["policyVersion"]?.DeepClone()??JsonValue.Create(SkillSemanticMatching.Version),["inputVersion"]=p["inputVersion"]?.DeepClone()??JsonValue.Create("legacy-json-string-v1"),
            ["threshold"]=choice?null:JsonValue.Create(SkillSemanticMatching.Threshold),["clearNegativeRouting"]=choice?null:JsonValue.Create(SkillSemanticMatching.ClearNegative),
            ["phase"]=p["phase"]!.DeepClone(),["outcome"]=stale?"requires_review":p["outcome"]?.DeepClone(),["reason"]=stale?"DICTIONARY_CHANGED":p["reason"]?.DeepClone(),
            ["choice"]=p["choice"]?.DeepClone(),["candidateSetHash"]=p["candidateSetHash"]?.DeepClone(),["comparisons"]=p["comparisons"]?.DeepClone()??new JsonArray(),["basis"]=p["basis"]!.DeepClone(),["shortlistComplete"]=p["shortlistComplete"]!.DeepClone(),
            ["selectedCount"]=p.Strings("targetIds").Length,["judgedCount"]=choice?(p["choice"] is null?0:p.Strings("targetIds").Length):p["comparisons"]!.AsArray().Count(c=>c?["forward"] is not null&&c?["reverse"] is not null),["applicableCount"]=p.Number("applicableCount"),["aiRunId"]=p["aiRunId"]?.DeepClone(),
            ["inferenceNotDispatched"]=p["inferenceNotDispatched"]?.DeepClone(),["providerExecutionUncertain"]=p["providerExecutionUncertain"]?.DeepClone()??JsonValue.Create(false),["stale"]=stale,
            ["requiresHumanApproval"]=true,["experienceChanged"]=false,["dictionaryChanged"]=false,["goldChanged"]=false,
            ["concepts"]=Node(snapshot.Concepts.Where(c=>p.Strings("targetIds").Contains(c.Id)).ToArray())
        };
    }
    private async Task<(StoredRecord Row,StoredRecord Candidate,Source Source,SkillDictionarySnapshot Snapshot)> ValidateProposal(ActorAccess actor,string id,CancellationToken ct)
    {
        actor=await Operator(actor,ct);if(!actor.HasRole("member"))throw new DomainException(403,"FORBIDDEN");var p=RecordPartition.Catalog(actor.TenantId);
        var row=await repository.ReadAsync(p,id,ct)??throw new DomainException(404,"NOT_FOUND");
        if(row.Value.Text("kind")!="skillMatchProposal"||row.Value.Text("ownerId")!=actor.PrincipalId)throw new DomainException(404,"NOT_FOUND");
        var candidate=await Candidate(actor,row.Value.Text("candidateId"),ct);var source=await ReadSource(actor,candidate.Value.Text("fragmentId"),ct);
        if(source.Fingerprint!=row.Value.Text("sourceFingerprint"))throw new DomainException(409,"SKILL_SOURCE_CHANGED");
        var (_,snapshot)=await Snapshot(actor.TenantId,row.Value.Number("dictionaryVersion"),ct);
        var element=candidate.Value["element"]!.Deserialize<SkillElement>(ProjectionRules.Json)!;
        if(SnapshotHash(snapshot)!=row.Value.Text("snapshotHash"))throw new DomainException(409,"SKILL_PROPOSAL_INVALID");
        if(row.Value.Text("policyVersion")==SkillChoiceMatching.Version)
        {
            var all=SkillChoiceMatching.Applicable(snapshot,candidate.Value.Strings("scopeIds"),actor.ScopeIds);
            var expected=all.Length>SkillChoiceMatching.MaxConcepts?Array.Empty<string>():all.Select(c=>c.Id).ToArray();
            if(!row.Value.Strings("targetIds").SequenceEqual(expected)||row.Value.Text("candidateSetHash")!=Hash(RecordJson.Canonical(Node(all.Select(c=>c.Id).ToArray()))))throw new DomainException(409,"SKILL_PROPOSAL_INVALID");
        }
        else if(row.Value.Text("policyVersion")!=SkillSemanticMatching.Version||row.Value.Strings("targetIds").Any(id=>!SkillSemanticMatching.Applicable(snapshot,element,candidate.Value.Strings("scopeIds")).Any(c=>c.Id==id)))throw new DomainException(409,"SKILL_PROPOSAL_INVALID");
        return(row,candidate,source,snapshot);
    }
    // Invoked inside AiService's normal fence, before cache or provider dispatch.
    internal async Task RequireSemanticInputUnderFenceAsync(AiContext context,string text,ChoiceQuestion[] choices,NoulQuestion[] nouls,CancellationToken ct)
    {
        var (row,candidate,source,snapshot)=await ValidateProposal(context.Actor,context.JobId,ct);
        var element=candidate.Value["element"]!.Deserialize<SkillElement>(ProjectionRules.Json)!;var item=candidate.Value["item"]!.Deserialize<SpecificSkill>(ProjectionRules.Json)!;
        var concepts=row.Value.Strings("targetIds").Select(id=>snapshot.Concepts.Single(c=>c.Id==id)).ToArray();
        var isChoice=row.Value.Text("policyVersion")==SkillChoiceMatching.Version;
        var expectedText=isChoice?SkillChoiceMatching.Input(item,element,concepts):SkillSemanticMatching.Input(item,element,concepts);
        if(isChoice&&(row.Value.Text("wireVersion")!=SkillChoiceWire.Version||row.Value.Text("probabilityPolicyVersion")!=ChoiceProbabilityRules.Version))throw new DomainException(409,"SKILL_PROPOSAL_INVALID");
        if(row.Value.Text("inputVersion")!=SkillSemanticBudget.Version||row.Value.Text("phase")!="dispatching"||context.PromptVersion!=row.Value.Text("policyVersion")||context.DataVersion!=snapshot.Version||context.Candidates.Length!=0||
            RecordJson.Canonical(Node(context.Evidence))!=RecordJson.Canonical(Node(References(source.Fragment.Value)))||
            RecordJson.Canonical(Node(choices))!=RecordJson.Canonical(Node(isChoice?new[]{SkillChoiceMatching.Question(concepts)}:Array.Empty<ChoiceQuestion>()))||
            RecordJson.Canonical(Node(nouls))!=RecordJson.Canonical(Node(isChoice?Array.Empty<NoulQuestion>():SkillSemanticMatching.Questions(concepts.Length)))||text!=expectedText)throw new DomainException(409,"SKILL_PROPOSAL_INVALID");
        var (_,current)=await Snapshot(context.Actor.TenantId,null,ct);
        if(current.Version!=snapshot.Version)throw new DomainException(409,"SKILL_DICTIONARY_CHANGED");
    }
    public async Task<JsonObject> ReadSuggestionUnderFenceAsync(ActorAccess actor,string candidateId,int version,CancellationToken ct)
    {
        actor=await Operator(actor,ct);var candidate=await Candidate(actor,candidateId,ct);var (_,snapshot)=await Snapshot(actor.TenantId,version,ct);
        var row=await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId),SuggestionId(actor,candidate.Value,snapshot),ct);
        var (_,head)=await Snapshot(actor.TenantId,null,ct);
        JsonObject result;
        if(row is null)result=new(){["phase"]="not_evaluated",["method"]="choice",["policyVersion"]=SkillChoiceMatching.Version,["outcome"]="requires_review",["dictionaryVersion"]=version,["reason"]="NOT_REQUESTED",["requiresHumanApproval"]=true};
        else {var valid=await ValidateProposal(actor,row.Value.Text("id"),ct);result=View(valid.Row.Value,valid.Snapshot,head.Version!=snapshot.Version);}
        var previous=new JsonArray();
        foreach(var legacy in new[]{false,true})
        {
            var old=await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId),NoulSuggestionId(actor,candidate.Value,snapshot,legacy),ct);
            if(old is not null){var valid=await ValidateProposal(actor,old.Value.Text("id"),ct);previous.Add(View(valid.Row.Value,valid.Snapshot,head.Version!=snapshot.Version));}
        }
        result["previousProposals"]=previous;return result;
    }
    public async Task<JsonObject> SuggestAsync(ActorAccess actor,string candidateId,string key,string etag,SkillSuggestionRequest input,CancellationToken ct)
    {
        IdempotencyExecutor.ValidateHeaders(key,etag,true);
        var proposalId=await fence.RunAsync(actor.TenantId,async token=>
        {
            actor=await Operator(actor,token);if(!actor.HasRole("member"))throw new DomainException(403,"FORBIDDEN");var candidate=await Candidate(actor,candidateId,token);var source=await ReadSource(actor,candidate.Value.Text("fragmentId"),token);
            var (head,snapshot)=await Snapshot(actor.TenantId,input.Version,token);var current=head?.Value.Number("version")??0;
            if(current!=input.Version||(head?.ETag??EmptyETag)!=input.DictionaryETag)throw new DomainException(412,"STALE_SKILL_DICTIONARY");
            var p=RecordPartition.Catalog(actor.TenantId);var id=SuggestionId(actor,candidate.Value,snapshot);
            var response=await idempotency.ExecuteAsync(p,actor,"POST","skill-candidates/"+candidateId+"/suggest/"+SkillChoiceMatching.Version,key,etag,Node(new{input.Version,input.DictionaryETag,policyVersion=SkillChoiceMatching.Version,inputVersion=SkillSemanticBudget.Version,wireVersion=SkillChoiceWire.Version,probabilityPolicyVersion=ChoiceProbabilityRules.Version,snapshotHash=SnapshotHash(snapshot),candidateSetHash=CandidateSetHash(actor,candidate.Value,snapshot)}),async()=>
            {
                if(candidate.ETag!=etag)throw new DomainException(412,"STALE_SKILL_CANDIDATE");
                if(candidate.Value.Text("state") is not ("pending" or "deferred" or "matched"))throw new DomainException(409,"SKILL_ALREADY_REVIEWED");
                if(await repository.ReadAsync(p,id,token) is not null)return new([],null,new(200,new JsonObject{["proposalId"]=id},null,null));
                var element=candidate.Value["element"]!.Deserialize<SkillElement>(ProjectionRules.Json)!;
                var applicable=SkillChoiceMatching.Applicable(snapshot,candidate.Value.Strings("scopeIds"),actor.ScopeIds);var targets=applicable.Length>SkillChoiceMatching.MaxConcepts?[]:applicable;
                var row=RecordJson.New("skillMatchProposal",id,actor.TenantId,actor.PrincipalId,candidate.Value.Strings("scopeIds"),clock.GetUtcNow());
                row["candidateId"]=candidateId;row["sourceFingerprint"]=source.Fingerprint;row["dictionaryVersion"]=snapshot.Version;row["snapshotHash"]=SnapshotHash(snapshot);
                row["policyVersion"]=SkillChoiceMatching.Version;row["wireVersion"]=SkillChoiceWire.Version;row["probabilityPolicyVersion"]=ChoiceProbabilityRules.Version;row["inputVersion"]=SkillSemanticBudget.Version;row["phase"]="prepared";row["outcome"]="requires_review";row["reason"]="NOT_EVALUATED";row["basis"]="choice";
                row["targetIds"]=RecordJson.Array(targets.Select(c=>c.Id));row["applicableCount"]=applicable.Length;row["shortlistComplete"]=targets.Length==applicable.Length;row["candidateSetHash"]=Hash(RecordJson.Canonical(Node(applicable.Select(c=>c.Id).ToArray())));row["choice"]=null;row["comparisons"]=new JsonArray();
                row["inferenceNotDispatched"]=true;row["providerExecutionUncertain"]=false;row["aiRunId"]=null;
                if(applicable.Length==0){row["basis"]="empty_scoped_catalogue";CompleteProposal(row,new("new_candidate","EMPTY_CHOICE_CATALOGUE",[]));}
                else if(applicable.Length>SkillChoiceMatching.MaxConcepts)CompleteProposal(row,new("requires_review","CHOICE_OPTION_LIMIT",[]));
                return new([RecordWrite.Create(row)],null,new(200,new JsonObject{["proposalId"]=id},null,null));
            },token);
            return response.Body!["proposalId"]!.GetValue<string>();
        },ct);
        // Durable claim survives response loss/crash. Neither another key nor a retry can claim again.
        var dispatch=await fence.RunAsync(actor.TenantId,async token=>
        {
            var valid=await ValidateProposal(actor,proposalId,token);var row=valid.Row.Value;
            if(row.Text("phase")!="prepared"||row.Text("inputVersion")!=SkillSemanticBudget.Version||row.Text("policyVersion")!=SkillChoiceMatching.Version)return false;
            var (_,head)=await Snapshot(actor.TenantId,null,token);
            if(head.Version!=valid.Snapshot.Version)CompleteProposal(row,new("requires_review","DICTIONARY_CHANGED",[]));
            else {row["phase"]="dispatching";row["reason"]="DISPATCHED_AWAITING_RESULT";}
            row["sourceVersion"]=row.Number("sourceVersion")+1;row["updatedAt"]=clock.GetUtcNow();
            await repository.CommitAsync(RecordPartition.Catalog(actor.TenantId),[RecordWrite.Replace(row,valid.Row.ETag)],token);return row.Text("phase")=="dispatching";
        },ct);
        if(dispatch)
        {
            AiResult<Decisions>? result=null;SkillSuggestionDecision decision;SkillChoiceScore? score=null;bool uncertain=false;
            try
            {
                var material=await fence.RunAsync(actor.TenantId,token=>ValidateProposal(actor,proposalId,token),ct);
                var row=material.Row.Value;var element=material.Candidate.Value["element"]!.Deserialize<SkillElement>(ProjectionRules.Json)!;var item=material.Candidate.Value["item"]!.Deserialize<SpecificSkill>(ProjectionRules.Json)!;
                var concepts=row.Strings("targetIds").Select(id=>material.Snapshot.Concepts.Single(c=>c.Id==id)).ToArray();
                var comparisonText=SkillChoiceMatching.Input(item,element,concepts);
                if(comparisonText.Length>64000)decision=new("requires_review","AI_INPUTLIMIT",[]);
                else if(decisionFactory is null)decision=new("requires_review","AI_UNAVAILABLE",[]);
                else
                {
                    var context=new AiContext(actor,proposalId,SkillChoiceMatching.Version,"taxonomy-v1",material.Snapshot.Version,References(material.Source.Fragment.Value),[]);
                    result=await decisionFactory().DecideAsync(context,comparisonText,[SkillChoiceMatching.Question(concepts)],[],ct);
                    if(result.Status==AiStatus.Succeeded&&result.Value is not null)(decision,score)=SkillChoiceMatching.Assess(concepts,element,result.Value);
                    else decision=new("requires_review","AI_"+(result.Failure?.ToString().ToUpperInvariant()??"NOT_EVALUATED"),[]);
                    uncertain=!result.InferenceNotDispatched&&!result.ProviderRejected&&result.Failure is (AiFailure.Timeout or AiFailure.Cancelled or AiFailure.Unavailable or AiFailure.OutputLimit);
                }
            }
            catch(DomainException e) when(e.Status<500){await FailUnexposed(actor,proposalId,e.Code);throw;}
            catch(Exception e) when(e is IOException or OperationCanceledException){decision=new("requires_review","PROVIDER_EXECUTION_UNCERTAIN",[]);uncertain=true;}
            using var finalize=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await fence.RunAsync(actor.TenantId,async token=>
                {
                    var valid=await ValidateProposal(actor,proposalId,token);var (_,head)=await Snapshot(actor.TenantId,null,token);
                    if(head.Version!=valid.Snapshot.Version)decision=new("requires_review","DICTIONARY_CHANGED",decision.Comparisons);
                    var row=valid.Row.Value;row["inferenceNotDispatched"]=result?.InferenceNotDispatched??!uncertain;row["choice"]=score is null?null:Node(score);CompleteProposal(row,decision,result?.RunId,uncertain);row["sourceVersion"]=row.Number("sourceVersion")+1;row["updatedAt"]=clock.GetUtcNow();
                    await repository.CommitAsync(RecordPartition.Catalog(actor.TenantId),[RecordWrite.Replace(row,valid.Row.ETag)],token);return true;
                },finalize.Token);
            }
            catch(DomainException e) when(e.Status<500){await FailUnexposed(actor,proposalId,e.Code,result?.RunId);throw;}
        }
        return await fence.RunAsync(actor.TenantId,async token=>
        {
            var valid=await ValidateProposal(actor,proposalId,token);var (_,head)=await Snapshot(actor.TenantId,null,token);
            return await ReadSuggestionUnderFenceAsync(actor,valid.Candidate.Value.Text("id"),valid.Snapshot.Version,token);
        },ct);
    }
    private async Task FailUnexposed(ActorAccess actor,string id,string reason,string? runId=null)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fence.RunAsync(actor.TenantId,async token=>
        {
            var p=RecordPartition.Catalog(actor.TenantId);var stored=await repository.ReadAsync(p,id,token);
            if(stored is not null){var row=stored.Value;CompleteProposal(row,new("requires_review",reason,[]),runId);row["updatedAt"]=clock.GetUtcNow();row["sourceVersion"]=row.Number("sourceVersion")+1;await repository.CommitAsync(p,[RecordWrite.Replace(row,stored.ETag)],token);}return true;
        },timeout.Token);
    }
}
