using System.Text;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

// Stable IDs stay authoritative on the server. Fixed wire IDs reduce repeated opaque GUIDs
// without filtering concepts or changing names/definitions/conditions/probability values.
public static class SkillChoiceWire
{
    public const string Version="skill-choice-wire-v2";
    public const int NumericBytes=32;
    public static Dictionary<string,string> IdMap(ChoiceQuestion question)
    {
        var i=0;return question.Options.Keys.ToDictionary(id=>id is SkillChoiceMatching.None or SkillChoiceMatching.Uncertain?id:"c"+(i++).ToString("D3"),id=>id,StringComparer.Ordinal);
    }
    public static Dictionary<string,string> Options(ChoiceQuestion question)=>IdMap(question).ToDictionary(p=>p.Key,p=>question.Options[p.Value],StringComparer.Ordinal);
    public static JsonObject Prepare(JsonObject input,ChoiceQuestion question)
    {
        var copy=input.DeepClone().AsObject();var dictionary=copy["text"]!["dictionary"]!.AsObject();var compact=new JsonObject();var rows=new JsonArray();var rowIds=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(var (wire,id) in IdMap(question))
            if(id is not (SkillChoiceMatching.None or SkillChoiceMatching.Uncertain))
            {
                var entry=dictionary[id]!;var aliases=new JsonArray();
                foreach(var alias in entry["aliases"]!.AsArray())aliases.Add(new JsonArray(alias!["text"]!.GetValue<string>()==entry["name"]!.GetValue<string>()?null:alias["text"]!.DeepClone(),alias["kind"]!.DeepClone(),alias["object"]?.DeepClone(),alias["conditions"]!.DeepClone()));
                var row=new JsonArray(entry["definition"]?.DeepClone(),entry["boundary"]?.DeepClone(),aliases);var key=RecordJson.Canonical(row);
                if(!rowIds.TryGetValue(key,out var index)){index=rows.Count;rowIds[key]=index;rows.Add(row);}
                compact[wire]=index;
            }
        if(compact.Count!=dictionary.Count)throw new ArgumentException("Choice input catalogue mismatch");
        copy["text"]!["dictionary"]=compact;
        copy["text"]!["dictionaryRows"]=rows;
        copy["text"]!["dictionaryLayout"]=new JsonArray("definitionOrShared","boundaryOrShared","aliases");
        copy["text"]!["aliasLayout"]=new JsonArray("text","kind","object","conditions");
        copy["text"]!["layoutPolicy"]="dictionary値はdictionaryRowsの0始まり行番号。各行はdictionaryLayout順、別名はaliasLayout順。名前は同じIDのcriteria全文。定義/境界nullはsharedDefinition/sharedBoundary全文、別名text=nullはその名前全文。共有は完全一致のみ・省略/要約なし。";
        copy["choiceWireVersion"]=Version;return copy;
    }
    // A shape estimate for the bounded response contract, using the exact wire IDs.
    // Numbers longer than NumericBytes are rejected by the decoder. Ordinary provider
    // byte/usage limits and the configured full token reservation still apply afterwards.
    public static int OutputBound(AiModel model,ChoiceQuestion question)
    {
        var number=JsonNode.Parse("0."+new string('1',NumericBytes-2))!;var probabilities=new JsonObject();var ids=IdMap(question).Keys.ToArray();
        foreach(var id in ids)probabilities[id]=number.DeepClone();
        var response=new JsonObject{["model"]=model.ModelId,["answers"]=new JsonObject{[question.Id]=new JsonObject{["type"]="choice",["choice"]=ids.MaxBy(Encoding.UTF8.GetByteCount),["probabilities"]=probabilities,["confidence"]=number.DeepClone()}},["usage"]=new JsonObject{["input_tokens"]=long.MaxValue,["output_tokens"]=long.MaxValue}};
        return Encoding.UTF8.GetByteCount(SkillSemanticBudget.Serialize(response))+256;
    }
    internal static bool BoundedNumber(JsonNode? node)=>node is not null&&Encoding.UTF8.GetByteCount(node.ToJsonString())<=NumericBytes;
}
