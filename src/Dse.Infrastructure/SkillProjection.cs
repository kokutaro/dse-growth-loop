using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed partial class SkillDictionaryService
{
    private sealed record SkillProjectionPlan(RecordWrite[] Primary,RecordWrite[] Projections);
    private async Task<SkillProjectionPlan> PrepareSkillProjection(ActorAccess actor,Source source,SkillDictionarySnapshot snapshot,IEnumerable<JsonObject> staged,CancellationToken ct)
    {
        // Read-only service instances intentionally have no transaction writer.
        if(!idempotency.HasProjectionTransaction||source.Fragment.Value["skillHistoryOnly"]?.GetValue<bool>()==true)return new([],[]);
        var p=RecordPartition.Catalog(actor.TenantId);var fragment=source.Fragment.Value;var values=new Dictionary<string,JsonObject>();string? cursor=null;var pages=0;
        do{
            if(++pages>100)throw new DomainException(422,"SKILL_SEARCH_TOO_LARGE");
            var page=await repository.QueryAsync(p,"skillCandidate","fragmentId",fragment.Text("id"),100,cursor,ct);
            foreach(var row in page.Items)values[row.Value.Text("id")]=row.Value;cursor=page.ContinuationToken;
        }while(cursor is not null);
        foreach(var row in staged.Where(v=>v.Text("kind")=="skillCandidate"))values[row.Text("id")]=row;
        var bindings=new List<ApprovedSkillBinding>();
        foreach(var row in values.Values.Where(v=>v.Text("candidateType")!="collection"&&v.Text("sourceFingerprint")==source.Fingerprint&&v.Text("state") is "registered" or "alias" or "matched" or "merged"))
        {
            if(SyntheticTestAccess.IsTest(row))continue;
            var element=row["element"]!.Deserialize<SkillElement>(ProjectionRules.Json)!;var match=SkillDictionaryRules.Match(snapshot,element,row.Strings("scopeIds"));
            var eligibility=row["item"]?["analysis"]?["experienceState"]?.GetValue<string>();
            if(match.ConceptId is not null&&eligibility is "content_only" or "eligible")bindings.Add(new(row.Text("id"),match.ConceptId,eligibility));
        }
        var headId="skill-projection-head-"+Hash(fragment.Text("id"))[..48];var previous=await repository.ReadAsync(p,headId,ct);
        if(bindings.Count==0&&previous is null)return new([],[]);
        var refs=fragment.Text("kind")=="knowledge"?new[]{new EvidenceReference(fragment.Text("id"),fragment.Text("id"),fragment.Number("activeVersion"))}:fragment["evidenceRefs"]?.Deserialize<EvidenceReference[]>(ProjectionRules.Json)??[];
        List<(JsonObject Entity,ApprovedSkillBinding[] Bindings)> targets=[];
        var content=bindings.Where(b=>b.Eligibility=="content_only").ToArray();
        if(content.Length>0&&refs.Length>0)
        {
            JsonObject? entity=fragment.Text("kind")=="knowledge"?fragment:null;
            if(entity is null){var evidence=await guard.EvidenceAsync(actor,refs[0],ct);entity=evidence?.Document;}
            if(entity is not null)targets.Add((entity,content));
        }
        var eligible=bindings.Where(b=>b.Eligibility=="eligible").ToArray();
        if(eligible.Length>0)
        {
            var entity=fragment.Text("kind")=="experience"?(await repository.ReadAsync(p,fragment.Text("personId"),ct))?.Value:fragment;
            if(entity is not null&&AccessPolicy.CanUse(actor,entity))targets.Add((entity,eligible));
        }
        var projections=new List<RecordWrite>();var ids=new List<string>();
        foreach(var (entity,approved) in targets)
        {
            var id="skill-unit-"+Hash(fragment.Text("id")+":"+entity.Text("id")+":"+source.Fingerprint+":"+snapshot.Version+":"+RecordJson.Canonical(Node(approved)));ids.Add(id);
            if(previous?.Value.Strings("projectionIds").Contains(id)==true)continue;
            var unit=new SearchUnit(id,entity.Text("id"),entity.Text("kind"),actor.TenantId,fragment.Text("id"),entity.Text("kind")=="person"?entity.Text("displayName"):entity.Text("title"),fragment.Text(fragment.Text("kind")=="knowledge"?"summary":"description"),fragment.Strings("scopeIds"),"active",true,Retriever.TaxonomyVersion,[],[],[],[],"human_skill_review")
            {SourceVersion=entity.Number("activeVersion"),ProjectionVersion=entity.Number("activeVersion"),AccessVersion=entity.Number("accessVersion"),ProjectionState="ready",EvidenceRefs=refs,SpecificSkills=source.Skills,SkillIds=approved.Select(b=>b.ConceptId).Distinct().ToArray(),SkillBindings=approved,SkillDictionaryVersion=snapshot.Version,SkillProjectionHeadId=headId};
            var value=Node(unit).AsObject();foreach(var property in RecordJson.New("searchUnit",id,actor.TenantId,actor.PrincipalId,fragment.Strings("scopeIds"),clock.GetUtcNow()))if(!value.ContainsKey(property.Key))value[property.Key]=property.Value?.DeepClone();
            projections.Add(RecordWrite.Create(value));
        }
        if(previous is not null&&previous.Value.Strings("projectionIds").SequenceEqual(ids))return new([],projections.ToArray());
        var head=previous?.Value.Copy()??RecordJson.New("skillProjectionHead",headId,actor.TenantId,actor.PrincipalId,fragment.Strings("scopeIds"),clock.GetUtcNow());
        head["fragmentId"]=fragment.Text("id");head["sourceFingerprint"]=source.Fingerprint;head["projectionIds"]=RecordJson.Array(ids);head["dictionaryVersion"]=snapshot.Version;head["updatedAt"]=clock.GetUtcNow();
        return new([new(head,previous?.ETag)],projections.ToArray());
    }

    internal async Task<SearchUnit?> ResolveSkillProjectionUnderFenceAsync(ActorAccess actor,SearchUnit unit,CancellationToken ct)
    {
        if(unit.SkillProjectionHeadId is null)return unit.SkillBindings is null?unit:null;
        var head=await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId),unit.SkillProjectionHeadId,ct);
        if(head?.Value.Text("kind")!="skillProjectionHead"||head.Value.Text("fragmentId")!=unit.UnitId||!head.Value.Strings("projectionIds").Contains(unit.Id)||unit.SkillBindings is not {Length:>0 and <=40})return null;
        var source=await ReadSource(actor,unit.UnitId,ct);if(source.Superseded||source.Fingerprint!=head.Value.Text("sourceFingerprint"))return null;
        if(!actor.HasRole("operator"))return unit with{SkillIds=[],SkillBindings=[]};
        var (_,snapshot)=await Snapshot(actor.TenantId,unit.SkillViewVersion,ct);var approved=new List<ApprovedSkillBinding>();
        foreach(var binding in unit.SkillBindings)
        {
            var row=await Candidate(actor,binding.CandidateId,ct);
            if(row.Value.Text("state") is not ("registered" or "alias" or "matched" or "merged")||SyntheticTestAccess.IsTest(row.Value))continue;
            var match=SkillDictionaryRules.Match(snapshot,row.Value["element"]!.Deserialize<SkillElement>(ProjectionRules.Json)!,row.Value.Strings("scopeIds"));
            var eligibility=row.Value["item"]?["analysis"]?["experienceState"]?.GetValue<string>();
            if(match.ConceptId is not null&&eligibility is "content_only" or "eligible")approved.Add(new(binding.CandidateId,match.ConceptId,eligibility));
        }
        return unit with{SkillIds=approved.Select(b=>b.ConceptId).Distinct().ToArray(),SkillBindings=approved.ToArray(),SkillDictionaryVersion=snapshot.Version};
    }
}
