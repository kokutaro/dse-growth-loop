using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

// Request-local IDs reduce repeated long storage keys. This is a bijection, never model-created IDs.
public static class WorkbookWire
{
    public const string Version="workbook-wire-v2";
    public static (JsonObject Input, Dictionary<string,string> Map) Prepare(JsonObject input)
    {
        var copy = input.DeepClone().AsObject(); var map = new Dictionary<string,string>(); int i=0;
        var sheets=new Dictionary<string,string>();var sheetNames=new JsonObject();var formats=new JsonObject();var formatIds=new Dictionary<string,string>();
        foreach(var entry in copy["evidence"]!.AsArray())
        {
            var e=entry!.AsObject();var id=$"c{i++}";map.Add(id,e.Text("id"));e["id"]=id;
            e.Remove("documentVersionId");e.Remove("sourceVersion");
            if(e["sourceCell"] is JsonObject cell)
            {
                var name=cell.Text("sheet");
                if(!sheets.TryGetValue(name,out var sheetId)){sheetId=$"s{sheets.Count}";sheets.Add(name,sheetId);sheetNames[sheetId]=new JsonObject{["name"]=name,["visibility"]=cell.Text("sheetState")};}
                e["at"]=sheetId+"!"+(cell["mergeRange"] is not null && cell.Text("mergeRange").Split(':')[0] == cell.Text("address") ? cell.Text("mergeRange") : cell.Text("address")+(cell["mergeRange"] is null?"":" / merged "+cell.Text("mergeRange")))+
                    (cell["hiddenRow"]?.GetValue<bool>()==true?" / hidden row":"")+(cell["hiddenColumn"]?.GetValue<bool>()==true?" / hidden column":"");
                var typed=cell.Deserialize<CellSource>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
                var date=WorkbookDates.Read(e.Text("text"),typed);
                if(date.Value is not null || typed.ValueType is "n" or "d" && typed.NumberFormat is {Length:>0})
                {
                    var code=typed.NumberFormat??"unspecified";
                    if(!formatIds.TryGetValue(code,out var f)){f="f"+formatIds.Count;formatIds.Add(code,f);formats[f]=new JsonObject{["code"]=code,["precision"]=date.Precision};}
                    e["format"]=f;e["displayValue"]=date.DisplayValue;
                }
                if(typed.Formula||typed.ValueState!="available"||typed.ValueType=="e")e["valueState"]=typed.Formula&&typed.ValueState=="available"?"formula_cached":typed.ValueState;
                e.Remove("sourceCell");e.Remove("location");
            }
        }
        var cells=new JsonObject();
        foreach(var entry in copy["evidence"]!.AsArray())
        {
            var e=entry!.DeepClone().AsObject();var id=e.Text("id");e.Remove("id");
            if(e["location"] is JsonObject location){e["at"]=location["label"]?.DeepClone();e.Remove("location");}
            cells[id]=e;
        }
        copy["cells"]=cells;copy.Remove("evidence");copy.Remove("candidates");copy["sheets"]=sheetNames;
        // The complete section text is already present once in evidence; no duplicated narrative.
        if(copy.Text("workbookPolicy")==WorkbookRules.CellSkillPrompt)
            copy["text"]="Read the complete source cells below as one section. Use sectionKind to distinguish project from non-project context.";
        if(formats.Count>0)copy["formats"]=formats;
        copy["workbookWireVersion"]=Version; return(copy,map);
    }
    public static string Serialize(JsonObject value) => value.ToJsonString(new System.Text.Json.JsonSerializerOptions {
        Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    internal static bool IsWorkbookRequest(JsonObject body)
    {
        if(body["state"]?["workbookWireVersion"]?.GetValue<string>()==Version)return true;
        if(body["messages"] is not JsonArray messages || messages.Count<2)return false;
        var content=messages[1]?["content"]?.GetValue<string>();
        if(content is null)return false;
        try{return JsonNode.Parse(content)?["workbookPolicy"]?.GetValue<string>() is WorkbookRules.Version or WorkbookRules.CellSkillPrompt;}
        catch(System.Text.Json.JsonException){return false;}
    }
    public static int RequestByteBound(AiModel model,string operation,JsonObject authoritativeInput,int outputTokens)
    {
        var (input,_)=Prepare(authoritativeInput);
        return System.Text.Encoding.UTF8.GetByteCount(AiCodecs.SerializeRequest(AiCodecs.FoundryRequest(model,operation,input,outputTokens)))+256;
    }
    public static T Restore<T>(T result, IReadOnlyDictionary<string,string> map) => result switch
    {
        WorkbookStructure w => (T)(object)(w with { Sections=w.Sections.Select(s=>s with { EvidenceIds=s.EvidenceIds.Select(id=>map[id]).ToArray(), StartEvidenceId=s.StartEvidenceId is null?null:map[s.StartEvidenceId], EndEvidenceId=s.EndEvidenceId is null?null:map[s.EndEvidenceId] }).ToArray() }),
        SpecificSkills s => (T)(object)(s with { Items=s.Items?.Select(i=>i with { EvidenceId=map.TryGetValue(i.EvidenceId,out var restored)?restored:"unmapped-"+OfficeExtractor.Hash(System.Text.Encoding.UTF8.GetBytes(i.EvidenceId)) }).ToArray() }),
        _ => result
    };
}
