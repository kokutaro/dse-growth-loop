using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

// Curation writes remain human-only. Optional proposals use the existing guarded AI
// provider; neither path updates gold, publishes experience, queues or search projections.
public sealed partial class SkillDictionaryService(IPrimaryRepository repository, AccessFence fence, IdempotencyExecutor idempotency, TimeProvider clock, Func<IDecisionProvider>? decisionFactory=null)
{
    public const string HeadId="skill-dictionary-head";
    public const string EmptyETag="\"skill-dictionary-empty\"";
    private static JsonNode Node<T>(T value)=>JsonSerializer.SerializeToNode(value,ProjectionRules.Json)!;
    private readonly AuthorityProvider authority=new(repository);
    private readonly SourceGuard guard=new(repository);
    private static string VersionId(int v)=>"skill-dictionary-version-"+v;
    private static string Hash(string s)=>ConversationService.Hash(s);
    private async Task<ActorAccess> Operator(ActorAccess actor,CancellationToken ct)
    {
        var current=await authority.RequireAsync(actor,ct);
        if(!current.HasRole("operator"))throw new DomainException(403,"FORBIDDEN");return current;
    }
    private async Task<(StoredRecord? Head,SkillDictionarySnapshot Snapshot)> Snapshot(string tenant,int? version,CancellationToken ct)
    {
        var p=RecordPartition.Catalog(tenant);var head=await repository.ReadAsync(p,HeadId,ct);var current=head?.Value.Number("version")??0;
        if(version<0||version>current)throw new DomainException(404,"SKILL_VERSION_NOT_FOUND");
        var v=version??current;if(v==0)return(head,new(0,[]));
        var row=await repository.ReadAsync(p,VersionId(v),ct)??throw new DomainException(503,"SKILL_DICTIONARY_UNAVAILABLE");
        return(head,row.Value["snapshot"]!.Deserialize<SkillDictionarySnapshot>(ProjectionRules.Json)!);
    }
    public async Task<JsonObject> ReadUnderFenceAsync(ActorAccess actor,int? version,CancellationToken ct)
    {
        actor=await Operator(actor,ct);var (head,snapshot)=await Snapshot(actor.TenantId,version,ct);
        return new(){["version"]=snapshot.Version,["etag"]=head?.ETag??EmptyETag,["currentVersion"]=head?.Value.Number("version")??0,
            ["concepts"]=Node(snapshot.Concepts.Where(c=>c.ScopeIds.Intersect(actor.ScopeIds).Any()).ToArray()),["contract"]=SkillDictionaryRules.Version};
    }
    private sealed record Source(StoredRecord Fragment,SpecificSkills Skills,string Fingerprint,Dictionary<string,string> Evidence);
    private async Task<Source> ReadSource(ActorAccess actor,string id,CancellationToken ct)
    {
        if(!RecordJson.IsId(id))throw new ArgumentException("Invalid fragment ID");
        var fragment=await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId),id,ct)??throw new DomainException(404,"NOT_FOUND");
        var value=fragment.Value;
        if(value.Text("kind") is not ("experience" or "knowledge" or "departmentActivity"))throw new DomainException(404,"NOT_FOUND");
        AccessPolicy.RequireRead(actor,value); // Includes private experience's self boundary; operator is not a bypass.
        if(value.Text("kind")!="experience"&&!AccessPolicy.CanUse(actor,value))throw new DomainException(404,"NOT_FOUND");
        var refs=value["evidenceRefs"]?.Deserialize<EvidenceReference[]>(ProjectionRules.Json)??[];
        if(value.Text("kind")=="knowledge")refs=[new(id,id,value.Number("activeVersion"))];
        var text=value.Text(value.Text("kind")=="knowledge"?"summary":"description");
        var evidence=new Dictionary<string,string>();var evidenceBindings=new JsonArray();
        foreach(var reference in refs)
        {
            var result=await guard.EvidenceAsync(actor,reference,ct)??throw new DomainException(404,"NOT_FOUND");
            evidence[reference.Id]=result.Span.Text("text");
            evidenceBindings.Add(new JsonObject{["id"]=reference.Id,["documentId"]=result.Document.Text("id"),["accessVersion"]=result.Document.Number("accessVersion"),["scopes"]=result.Document["scopeIds"]?.DeepClone(),["textHash"]=Hash(result.Span.Text("text"))});
        }
        var skills=value["specificSkills"]?.Deserialize<SpecificSkills>(ProjectionRules.Json)??SpecificSkills.NotEvaluated;
        var assessed=SpecificSkillRules.Assess(skills,evidence,text,value.Text("kind")=="experience"?"person":value.Text("kind"));
        var fingerprint=Hash(RecordJson.Canonical(new JsonObject{["version"]=value.Number("sourceVersion"),["scopes"]=value["scopeIds"]?.DeepClone(),["evidenceBindings"]=evidenceBindings,["text"]=text,["evidenceRefs"]=Node(refs),["skills"]=Node(assessed)}));
        return new(fragment,assessed,fingerprint,evidence);
    }
    private async Task<StoredRecord> Candidate(ActorAccess actor,string id,CancellationToken ct)
    {
        if(!RecordJson.IsId(id))throw new ArgumentException("Invalid candidate ID");
        var row=await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId),id,ct);
        if(row?.Value.Text("kind")!="skillCandidate"||!AccessPolicy.InScope(actor,row.Value))throw new DomainException(404,"NOT_FOUND");
        var source=await ReadSource(actor,row.Value.Text("fragmentId"),ct);
        if(source.Fingerprint!=row.Value.Text("sourceFingerprint"))throw new DomainException(409,"SKILL_SOURCE_CHANGED");
        if(row.Value.Text("candidateType")=="collection")throw new DomainException(409,"SKILL_COLLECTION_HELD");
        return row;
    }
    // Caller keeps the access fence through serialization. Inaccessible source
    // candidates do not reveal their IDs, quotes, counts or review history.
    public async Task<JsonObject> ListUnderFenceAsync(ActorAccess actor,string? cursor,CancellationToken ct)
    {
        actor=await Operator(actor,ct);
        var page=await repository.QueryAsync(RecordPartition.Catalog(actor.TenantId),"skillCandidate",null,null,50,cursor,ct);
        var items=new JsonArray();
        foreach(var row in page.Items)
        {
            try
            {
                if(!AccessPolicy.InScope(actor,row.Value))continue;
                var source=await ReadSource(actor,row.Value.Text("fragmentId"),ct);
                var changed=source.Fingerprint!=row.Value.Text("sourceFingerprint");
                // Never serialize an old quote/label, proposal or review reason after a
                // source change. Current access to the fragment is not access to old text.
                var value=changed?new JsonObject{
                    ["id"]=row.Value.Text("id"),["fragmentId"]=row.Value.Text("fragmentId"),
                    ["state"]=row.Value.Text("state"),["candidateType"]=row.Value.Text("candidateType"),
                    ["item"]=null,["element"]=null
                }:row.Value.Copy();
                value["etag"]=row.ETag;value["sourceStatus"]=changed?"changed":"current";
                value["reviewable"]=!changed&&row.Value.Text("candidateType")!="collection";
                items.Add(value);
            }
            catch(DomainException e) when(e.Status==404) { }
        }
        return new(){["items"]=items,["continuationToken"]=page.ContinuationToken};
    }
    public Task<MutationResponse> CollectAsync(ActorAccess actor,string key,SkillCandidateCollect input,CancellationToken ct)=>fence.RunAsync(actor.TenantId,async token=>
    {
        actor=await Operator(actor,token);var source=await ReadSource(actor,input.FragmentId,token);var p=RecordPartition.Catalog(actor.TenantId);
        if(input.ExpectedFingerprint is not null&&input.ExpectedFingerprint!=source.Fingerprint)throw new DomainException(409,"SKILL_SOURCE_CHANGED");
        return await idempotency.ExecuteAsync(p,actor,"POST","skill-candidates/collect",key,null,Node(new{input.FragmentId,source.Fingerprint}),async()=>
        {
            var (_,snapshot)=await Snapshot(actor.TenantId,null,token);var writes=new List<RecordWrite>();var bindings=new JsonArray();
            foreach(var item in source.Skills.Items??[])
            foreach(var element in item.Analysis?.Elements??[])
            {
                var id="skill-candidate-"+Hash(input.FragmentId+":"+source.Fingerprint+":"+RecordJson.Canonical(Node(new{item.Raw,item.Quote,item.EvidenceId,item.Participation,element})));
                var match=SkillDictionaryRules.Match(snapshot,element,source.Fragment.Value.Strings("scopeIds"));
                var existing=await repository.ReadAsync(p,id,token);
                if(existing is null)
                {
                    var value=RecordJson.New("skillCandidate",id,actor.TenantId,actor.PrincipalId,source.Fragment.Value.Strings("scopeIds"),clock.GetUtcNow());
                    value["fragmentId"]=input.FragmentId;value["sourceFingerprint"]=source.Fingerprint;value["sourceInputState"]=source.Skills.State;
                    value["item"]=Node(item);value["element"]=Node(element);value["dictionaryVersionAtCollection"]=snapshot.Version;
                    value["state"]=match.State=="mapped"?"matched":"pending";value["conceptId"]=match.ConceptId;value["history"]=new JsonArray();writes.Add(RecordWrite.Create(value));
                }
                bindings.Add(new JsonObject{["candidateId"]=id,["mapping"]=Node(match),["state"]=existing?.Value.Text("state")??(match.State=="mapped"?"matched":"pending")});
            }
            // A collection notice is not a skill and cannot be approved or sent to AI.
            // Persist only the outcome/reasons; unverified quotes are never copied.
            string? collectionId=null;
            if(source.Skills.RejectedItems?.Length>0)
            {
                collectionId="skill-candidate-"+Hash(input.FragmentId+":"+source.Fingerprint+":collection");
                if(await repository.ReadAsync(p,collectionId,token) is null)
                {
                    var notice=RecordJson.New("skillCandidate",collectionId,actor.TenantId,actor.PrincipalId,source.Fragment.Value.Strings("scopeIds"),clock.GetUtcNow());
                    notice["candidateType"]="collection";notice["fragmentId"]=input.FragmentId;notice["sourceFingerprint"]=source.Fingerprint;
                    notice["sourceInputState"]=source.Skills.State;notice["item"]=null;notice["element"]=null;notice["conceptId"]=null;
                    notice["state"]="collection_held";notice["dictionaryVersionAtCollection"]=snapshot.Version;notice["history"]=new JsonArray();
                    notice["holdReasons"]=RecordJson.Array((source.Skills.RejectedItems??[]).Select(r=>r.Reason).Distinct());
                    writes.Add(RecordWrite.Create(notice));
                }
            }
            if(writes.Count>90)throw new DomainException(422,"SKILL_COLLECTION_LIMIT"); // Never truncate candidates or break normal extraction.
            var state=source.Skills.State=="unknown"?"undetermined":source.Skills.State=="not_evaluated"?"not_evaluated":bindings.Count==0?(source.Skills.RejectedItems?.Length>0?"held":"none"):"assessed";
            return new MutationPlan(writes.ToArray(),null,new(200,new JsonObject{["dictionaryVersion"]=snapshot.Version,["inputState"]=source.Skills.State,["state"]=state,["bindings"]=bindings,["created"]=writes.Count(w=>w.Value.Text("candidateType")!="collection"),["collectionId"]=collectionId,["quarantined"]=source.Skills.RejectedItems?.Length??0},null,null));
        },token);
    },ct);
    // Read current evidence under the same tenant fence as serialization. Deleted,
    // withdrawn or inaccessible sources all stay 404; no tombstone leaks their existence.
    public async Task<JsonObject> SourceUnderFenceAsync(ActorAccess actor,string id,CancellationToken ct)
    {
        actor=await Operator(actor,ct);
        if(!RecordJson.IsId(id))throw new ArgumentException("Invalid candidate ID");
        var row=await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId),id,ct);
        if(row?.Value.Text("kind")!="skillCandidate"||!AccessPolicy.InScope(actor,row.Value))throw new DomainException(404,"NOT_FOUND");
        var source=await ReadSource(actor,row.Value.Text("fragmentId"),ct);
        var rejected=new JsonArray();
        foreach(var r in source.Skills.RejectedItems??[])
        {
            var text=source.Evidence.GetValueOrDefault(r.Item.EvidenceId);
            // Explain the failure without echoing a fabricated/withdrawn quote.
            rejected.Add(new JsonObject{["reason"]=r.Reason,
                ["raw"]=text?.Contains(r.Item.Raw,StringComparison.Ordinal)==true?r.Item.Raw:null,
                ["participation"]=r.Item.Participation});
        }
        return new(){["candidateId"]=id,["fragmentId"]=row.Value.Text("fragmentId"),
            ["sourceStatus"]=source.Fingerprint==row.Value.Text("sourceFingerprint")?"current":"changed",
            ["currentFingerprint"]=source.Fingerprint,["guardVersion"]=SpecificSkillRules.GuardVersion,
            ["inputState"]=source.Skills.State,["acceptedCount"]=source.Skills.Items?.Length??0,["rejected"]=rejected,
            ["currentText"]=source.Fragment.Value.Text(source.Fragment.Value.Text("kind")=="knowledge"?"summary":"description"),
            ["evidence"]=new JsonArray(source.Evidence.Select(e=>(JsonNode)new JsonObject{["id"]=e.Key,["text"]=e.Value}).ToArray()),
            ["requiresRecollection"]=true,["personalExperienceApproved"]=false};
    }
    public Task<MutationResponse> ReviewAsync(ActorAccess actor,string id,string key,string etag,SkillDictionaryReview input,CancellationToken ct)=>fence.RunAsync(actor.TenantId,async token=>
    {
        actor=await Operator(actor,token);var row=await Candidate(actor,id,token);var p=RecordPartition.Catalog(actor.TenantId);
        IdempotencyExecutor.ValidateHeaders(key,etag,true);
        return await idempotency.ExecuteAsync(p,actor,"POST","skill-candidates/"+id+"/review",key,etag,Node(input),async()=>
        {
            if(row.ETag!=etag)throw new DomainException(412,"STALE_SKILL_CANDIDATE");
            var (head,before)=await Snapshot(actor.TenantId,null,token);
            if(before.Version!=input.ExpectedVersion||(head?.ETag??EmptyETag)!=input.DictionaryETag)throw new DomainException(412,"STALE_SKILL_DICTIONARY");
            if(input.Action is not ("new" or "alias" or "merge" or "not_skill" or "defer")||string.IsNullOrWhiteSpace(input.Reason)||input.Reason.Length>500)throw new ArgumentException("Review action and reason required");
            if(input.Action!="merge"&&input.SourceId is not null||input.Action is "defer" or "not_skill" or "new"&&input.TargetId is not null)throw new ArgumentException("Unexpected concept ID");
            if(input.Action!="new"&&(input.Name is not null||input.Definition is not null||input.Boundary is not null))throw new ArgumentException("Unexpected new concept definition");
            if(input.Action!="merge"&&row.Value.Text("state") is not ("pending" or "deferred" or "matched"))throw new DomainException(409,"SKILL_ALREADY_REVIEWED");
            var element=row.Value["element"]!.Deserialize<SkillElement>(ProjectionRules.Json)!;var scopes=row.Value.Strings("scopeIds");
            foreach(var target in new[]{input.TargetId,input.SourceId}.Where(x=>x is not null))
                if(!before.Concepts.Any(c=>c.Id==target&&c.ScopeIds.Intersect(actor.ScopeIds).Any()))throw new DomainException(404,"NOT_FOUND");
            var newId="skill-"+Guid.NewGuid().ToString("N");var after=SkillDictionaryRules.Approve(before,input,element,scopes,newId);
            var value=row.Value.Copy();value["sourceVersion"]=value.Number("sourceVersion")+1;value["updatedAt"]=clock.GetUtcNow();
            value["state"]=input.Action switch{"new"=>"registered","alias"=>"alias","merge"=>"merged","not_skill"=>"not_skill",_=>"deferred"};
            value["conceptId"]=input.Action=="new"?newId:input.Action is "alias" or "merge"?input.TargetId:null;
            value["reviewedDictionaryVersion"]=after.Version;
            var reviewId="skill-review-"+Guid.NewGuid().ToString("N");value["history"]!.AsArray().Add(reviewId);
            if(value["history"]!.AsArray().Count>50)throw new DomainException(422,"SKILL_REVIEW_LIMIT");
            var audit=RecordJson.New("skillDictionaryReview",reviewId,actor.TenantId,actor.PrincipalId,scopes,clock.GetUtcNow());
            audit["candidateId"]=id;audit["action"]=input.Action;audit["reason"]=input.Reason;audit["previousVersion"]=before.Version;audit["approvedVersion"]=after.Version;
            audit["sourceId"]=input.SourceId;audit["targetId"]=value["conceptId"]?.DeepClone();audit["sourceFingerprint"]=row.Value.Text("sourceFingerprint");
            var writes=new List<RecordWrite>{RecordWrite.Replace(value,row.ETag),RecordWrite.Create(audit)};
            if(after.Version!=before.Version)
            {
                var version=RecordJson.New("skillDictionaryVersion",VersionId(after.Version),actor.TenantId,actor.PrincipalId,["system"],clock.GetUtcNow());
                version["snapshot"]=Node(after);version["reviewId"]=reviewId;
                var next=head?.Value.Copy()??RecordJson.New("skillDictionaryHead",HeadId,actor.TenantId,actor.PrincipalId,["system"],clock.GetUtcNow());
                next["version"]=after.Version;next["updatedAt"]=clock.GetUtcNow();writes.Add(RecordWrite.Create(version));writes.Add(new(next,head?.ETag));
            }
            return new MutationPlan(writes.ToArray(),null,new(200,new JsonObject{["candidateId"]=id,["state"]=value["state"]!.DeepClone(),["dictionaryVersion"]=after.Version,["conceptId"]=value["conceptId"]?.DeepClone(),["reviewId"]=reviewId},null,null));
        },token);
    },ct);
    public async Task<JsonObject> PreviewUnderFenceAsync(ActorAccess actor,SkillDictionaryPreview input,CancellationToken ct)
    {
        actor=await Operator(actor,ct);if(input.CandidateIds is null||input.CandidateIds.Length is <1 or >50||input.CandidateIds.Distinct().Count()!=input.CandidateIds.Length)throw new ArgumentException("Select 1..50 unique candidates");
        var (_,snapshot)=await Snapshot(actor.TenantId,input.Version,ct);var rows=new JsonArray();
        foreach(var id in input.CandidateIds)
        {
            var c=await Candidate(actor,id,ct);var element=c.Value["element"]!.Deserialize<SkillElement>(ProjectionRules.Json)!;
            var match=c.Value.Text("state")=="not_skill"?new SkillDictionaryMatch("not_skill",null):SkillDictionaryRules.Match(snapshot,element,c.Value.Strings("scopeIds"));
            rows.Add(new JsonObject{["candidateId"]=id,["state"]=c.Value.Text("state"),["mapping"]=Node(match),["element"]=Node(element),["experienceEligibility"]=c.Value["item"]?["analysis"]?["experienceState"]?.DeepClone()});
        }
        return new(){["dictionaryVersion"]=snapshot.Version,["previewOnly"]=true,["businessRecordsChanged"]=false,["goldChanged"]=false,["items"]=rows};
    }
    public async Task<JsonObject> HistoryUnderFenceAsync(ActorAccess actor,string id,CancellationToken ct)
    {
        actor=await Operator(actor,ct);var c=await Candidate(actor,id,ct);var rows=new JsonArray();
        foreach(var reviewId in c.Value.Strings("history"))
            rows.Add((await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId),reviewId,ct))!.Value.DeepClone());
        return new(){["candidateId"]=id,["items"]=rows};
    }
}
