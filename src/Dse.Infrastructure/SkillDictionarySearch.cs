using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed partial class SkillDictionaryService
{
    // Read-time dictionary join, not a rewrite of search projections or past reviews.
    // The endpoint holds the tenant fence through serialization, just like preview.
    public async Task<JsonObject> SearchUnderFenceAsync(ActorAccess actor,SkillDictionarySearch input,CancellationToken ct)
    {
        actor=await Operator(actor,ct);input.Validate();
        using var budget=new SearchBudget();
        var snapshot=await ReadUnderFenceAsync(actor,input.Version,ct);
        var items=new JsonArray();var total=0;var scanned=0;
        var concept=snapshot["concepts"]!.AsArray().Select(n=>n!.AsObject()).SingleOrDefault(c=>c.Text("id")==input.ConceptId&&c.Text("state")=="active");
        if(concept is not null)
        {
            string? cursor=null;
            do
            {
                var page=await ListUnderFenceAsync(actor,cursor,ct);
                scanned+=page["items"]!.AsArray().Count;
                if(scanned>5000)throw new DomainException(422,"SKILL_SEARCH_TOO_LARGE");
                var candidates=page["items"]!.AsArray().Select(n=>n!.AsObject()).Where(c=>
                    !SyntheticTestAccess.IsTest(c)&&c["reviewable"]?.GetValue<bool>()==true&&
                    c.Text("state") is "registered" or "alias" or "matched").ToArray();
                if(candidates.Length>0)
                {
                    var preview=await PreviewUnderFenceAsync(actor,new(input.Version,candidates.Select(c=>c.Text("id")).ToArray()),ct);
                    foreach(var match in preview["items"]!.AsArray().Select(n=>n!.AsObject()).Where(m=>
                        m["mapping"]?["conceptId"]?.GetValue<string>()==input.ConceptId&&m.Text("experienceEligibility")==input.Eligibility))
                    {
                        total++;if(items.Count>=input.Limit)continue;
                        var candidate=candidates.Single(c=>c.Text("id")==match.Text("candidateId"));
                        var skill=candidate["item"]!.AsObject();
                        var history=await HistoryUnderFenceAsync(actor,candidate.Text("id"),ct);
                        items.Add(new JsonObject{
                            ["candidateId"]=candidate.Text("id"),["fragmentId"]=candidate.Text("fragmentId"),
                            ["conceptId"]=input.ConceptId,["skillName"]=concept.Text("name"),["raw"]=skill.Text("raw"),
                            ["quote"]=skill.Text("quote"),["evidenceId"]=skill.Text("evidenceId"),["basis"]=skill.Text("basis"),
                            ["experienceEligibility"]=input.Eligibility,["reviewState"]=candidate.Text("state"),
                            ["reviews"]=history["items"]!.DeepClone()
                        });
                    }
                }
                cursor=page["continuationToken"]?.GetValue<string>();
            }while(cursor is not null);
        }
        return new(){["dictionaryVersion"]=input.Version,["conceptId"]=input.ConceptId,["items"]=items,
            ["beforeLimit"]=total,["readOnly"]=true,["ordinarySearchIntegrated"]=true,["externalAiCalls"]=0};
    }
}
