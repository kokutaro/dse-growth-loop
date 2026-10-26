using System.Text;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

// Stable IDs stay authoritative on the server. Fixed wire IDs reduce repeated opaque GUIDs
// without filtering concepts or changing names/definitions/conditions/probability values.
public static class SkillChoiceWire
{
    public const string Version="skill-choice-wire-v1";
    public const int NumericBytes=32;
    public static Dictionary<string,string> IdMap(ChoiceQuestion question)
    {
        var i=0;return question.Options.Keys.ToDictionary(id=>id is SkillChoiceMatching.None or SkillChoiceMatching.Uncertain?id:"c"+(i++).ToString("D3"),id=>id,StringComparer.Ordinal);
    }
    public static Dictionary<string,string> Options(ChoiceQuestion question)=>IdMap(question).ToDictionary(p=>p.Key,p=>question.Options[p.Value],StringComparer.Ordinal);
    public static JsonObject Prepare(JsonObject input,ChoiceQuestion question)
    {
        var copy=input.DeepClone().AsObject();var dictionary=copy["text"]!["dictionary"]!.AsObject();var compact=new JsonObject();
        foreach(var (wire,id) in IdMap(question))
            if(id is not (SkillChoiceMatching.None or SkillChoiceMatching.Uncertain))
            {
                var entry=dictionary[id]!;var aliases=new JsonArray();
                foreach(var alias in entry["aliases"]!.AsArray())aliases.Add(new JsonArray(alias!["text"]!.GetValue<string>()==entry["name"]!.GetValue<string>()?null:alias["text"]!.DeepClone(),alias["kind"]!.DeepClone(),alias["object"]?.DeepClone(),alias["conditions"]!.DeepClone()));
                compact[wire]=new JsonArray(entry["definition"]?.DeepClone(),entry["boundary"]?.DeepClone(),aliases);
            }
        if(compact.Count!=dictionary.Count)throw new ArgumentException("Choice input catalogue mismatch");
        copy["text"]!["dictionary"]=compact;
        copy["text"]!["dictionaryLayout"]=new JsonArray("definitionOrShared","boundaryOrShared","aliases");
        copy["text"]!["aliasLayout"]=new JsonArray("text","kind","object","conditions");
        copy["text"]!["layoutPolicy"]="辞書はdictionaryLayout順の配列。名前は同じIDのcriteria値全文。定義・境界のnullはsharedDefinition/sharedBoundary全文。別名はaliasLayout順でtextのnullは名前全文。全フィールドを保持し要約はない。";
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
