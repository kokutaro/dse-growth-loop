using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;

namespace Dse.Tests;

// Deliberately test-only: linked by the loopback MemoryRecordStore browser host.
// A dictionary snapshot is fixed; authorization, evidence and review state remain current.
public sealed class SkillDocumentDemo(SkillDictionaryService dictionary)
{
    private static readonly HashSet<string> Sources=["fragment-technical-list-a-distinct","fragment-technical-list-b-distinct"];

    // The caller holds the existing access fence through response serialization.
    public async Task<JsonObject> SearchUnderFenceAsync(ActorAccess actor,int version,string conceptId,string eligibility,CancellationToken ct)
    {
        if(!RecordJson.IsId(conceptId)||eligibility is not ("content_only" or "eligible"))throw new ArgumentException("Select a skill ID and knowledge or experience");
        var snapshot=await dictionary.ReadUnderFenceAsync(actor,version,ct);
        var items=new JsonArray();
        var concept=snapshot["concepts"]!.AsArray().Select(n=>n!.AsObject()).SingleOrDefault(c=>c.Text("id")==conceptId&&c.Text("state")=="active");
        if(concept is not null)
        {
            string? cursor=null;
            do
            {
                // Uses the normal current-evidence checks. Stale/withdrawn sources and
                // collection notices cannot become searchable by an old dictionary version.
                var page=await dictionary.ListUnderFenceAsync(actor,cursor,ct);
                var candidates=page["items"]!.AsArray().Select(n=>n!.AsObject()).Where(c=>
                    Sources.Contains(c.Text("fragmentId"))&&c["reviewable"]?.GetValue<bool>()==true&&
                    c.Text("state") is "registered" or "alias" or "matched").ToArray();
                if(candidates.Length>0)
                {
                    var preview=await dictionary.PreviewUnderFenceAsync(actor,new(version,candidates.Select(c=>c.Text("id")).ToArray()),ct);
                    foreach(var match in preview["items"]!.AsArray().Select(n=>n!.AsObject()).Where(m=>
                        m["mapping"]?["conceptId"]?.GetValue<string>()==conceptId&&m.Text("experienceEligibility")==eligibility))
                    {
                        var candidate=candidates.Single(c=>c.Text("id")==match.Text("candidateId"));
                        var skill=candidate["item"]!.AsObject();
                        var history=await dictionary.HistoryUnderFenceAsync(actor,candidate.Text("id"),ct);
                        items.Add(new JsonObject{
                            ["candidateId"]=candidate.Text("id"),["fragmentId"]=candidate.Text("fragmentId"),
                            ["conceptId"]=conceptId,["skillName"]=concept.Text("name"),["raw"]=skill.Text("raw"),
                            ["quote"]=skill.Text("quote"),["evidenceId"]=skill.Text("evidenceId"),
                            ["basis"]=skill.Text("basis"),["experienceEligibility"]=eligibility,
                            ["reviewState"]=candidate.Text("state"),["reviews"]=history["items"]!.DeepClone()
                        });
                    }
                }
                cursor=page["continuationToken"]?.GetValue<string>();
            }while(cursor is not null);
        }
        return new(){["dictionaryVersion"]=version,["conceptId"]=conceptId,["items"]=items,
            ["readOnly"]=true,["normalSearchIntegrated"]=false,["externalAiCalls"]=0};
    }
}
