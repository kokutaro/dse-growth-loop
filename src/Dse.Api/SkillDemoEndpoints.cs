using Dse.Domain;
using Dse.Infrastructure;
using System.Text.Json.Nodes;

public static class SkillDemoEndpoints
{
    public static void MapSkillDemo(this RouteGroupBuilder api)
    {
        api.MapPost("/skill-demo/start",async(HttpContext c,SkillDemoSessions sessions,CancellationToken ct)=>{
            var demo=await sessions.StartAsync(RecordEndpoints.Actor(c),ct);c.Response.Headers.CacheControl="no-store";
            return Results.Ok(new{id=demo.Id,expiresAt=demo.ExpiresAt,sharedRecordsChanged=false,externalAiCalls=0});
        });
        var group=api.MapGroup("/skill-demo/{sessionId}");
        group.MapGet("/skill-dictionary",(string sessionId,int? version,HttpContext c,SkillDemoSessions sessions)=>
            Read(c,sessions,sessionId,(d,t)=>d.Dictionary.ReadUnderFenceAsync(d.Actor,version,t)));
        group.MapGet("/skill-candidates",(string sessionId,string? continuationToken,HttpContext c,SkillDemoSessions sessions,PageTokens tokens)=>
            Read(c,sessions,sessionId,async(d,t)=>{
                var actor=d.Actor;var filter="skill-demo:"+sessionId+":50";
                var page=await d.Dictionary.ListUnderFenceAsync(actor,tokens.Decode(actor,filter,continuationToken),t);
                // Preserve the historical four rejected items as source-only notices.
                page["items"]=new JsonArray(page["items"]!.AsArray().Where(n=>n!["candidateType"]?.GetValue<string>()=="collection"||SkillDemoSessions.EditableSource(n["fragmentId"]!.GetValue<string>())).Select(n=>n!.DeepClone()).ToArray());
                page["continuationToken"]=tokens.Encode(actor,filter,page["continuationToken"]?.GetValue<string>());return page;
            }));
        group.MapGet("/skill-candidates/{id}/source",(string sessionId,string id,HttpContext c,SkillDemoSessions sessions)=>Read(c,sessions,sessionId,(d,t)=>d.Dictionary.SourceUnderFenceAsync(d.Actor,id,t)));
        group.MapGet("/skill-candidates/{id}/history",(string sessionId,string id,HttpContext c,SkillDemoSessions sessions)=>Read(c,sessions,sessionId,(d,t)=>d.Dictionary.HistoryUnderFenceAsync(d.Actor,id,t)));
        group.MapGet("/skill-candidates/{id}/suggestion",(string sessionId,string id,int version,HttpContext c,SkillDemoSessions sessions)=>Read(c,sessions,sessionId,(d,t)=>d.Dictionary.ReadSuggestionUnderFenceAsync(d.Actor,id,version,t)));
        group.MapPost("/skill-dictionary/preview",(string sessionId,SkillDictionaryPreview input,HttpContext c,SkillDemoSessions sessions)=>Read(c,sessions,sessionId,(d,t)=>d.Dictionary.PreviewUnderFenceAsync(d.Actor,input,t)));
        group.MapPost("/skill-dictionary/search",(string sessionId,SkillDictionarySearch input,HttpContext c,SkillDemoSessions sessions)=>Read(c,sessions,sessionId,(d,t)=>d.Dictionary.SearchUnderFenceAsync(d.Actor,input,t)));
        group.MapPost("/skill-candidates/{id}/suggest",async(string sessionId,HttpContext c,SkillDemoSessions sessions,CancellationToken ct)=>{
            await sessions.GetAsync(RecordEndpoints.Actor(c),sessionId,ct);throw new DomainException(409,"SKILL_DEMO_AI_DISABLED");
        });
        group.MapPost("/skill-candidates/collect",async(string sessionId,SkillCandidateCollect input,HttpContext c,SkillDemoSessions sessions,CancellationToken ct)=>{
            var demo=await sessions.GetAsync(RecordEndpoints.Actor(c),sessionId,ct);
            if(!SkillDemoSessions.EditableSource(input.FragmentId))throw new DomainException(409,"SKILL_DEMO_HISTORY_READ_ONLY");
            var result=await demo.Dictionary.CollectAsync(demo.Actor,c.Request.Headers["Idempotency-Key"].ToString(),input,ct);
            await sessions.GetAsync(RecordEndpoints.Actor(c),sessionId,ct);c.Response.Headers.CacheControl="no-store";return Results.Json(result.Body,statusCode:result.Status);
        });
        group.MapPost("/skill-candidates/{id}/review",async(string sessionId,string id,SkillDictionaryReview input,HttpContext c,SkillDemoSessions sessions,CancellationToken ct)=>{
            var demo=await sessions.GetAsync(RecordEndpoints.Actor(c),sessionId,ct);
            var row=await demo.Repository.ReadAsync(RecordPartition.Catalog(demo.Actor.TenantId),id,ct);
            if(row?.Value.Text("kind")!="skillCandidate")throw new DomainException(404,"NOT_FOUND");
            if(!SkillDemoSessions.EditableSource(row.Value.Text("fragmentId")))throw new DomainException(409,"SKILL_DEMO_HISTORY_READ_ONLY");
            var result=await demo.Dictionary.ReviewAsync(demo.Actor,id,c.Request.Headers["Idempotency-Key"].ToString(),c.Request.Headers.IfMatch.ToString(),input,ct);
            await sessions.GetAsync(RecordEndpoints.Actor(c),sessionId,ct);c.Response.Headers.CacheControl="no-store";return Results.Json(result.Body,statusCode:result.Status);
        });
    }
    private static IResult Read(HttpContext c,SkillDemoSessions sessions,string id,Func<SkillDemoSession,CancellationToken,Task<JsonObject>> build)=>new DemoRead(c,sessions,id,build);
    private sealed class DemoRead(HttpContext request,SkillDemoSessions sessions,string id,Func<SkillDemoSession,CancellationToken,Task<JsonObject>> build):IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            var actor=RecordEndpoints.Actor(request);var demo=await sessions.GetAsync(actor,id,context.RequestAborted);
            // The memory lease covers serialization. Real authority is checked again;
            // no shared business fence/write is needed for this operator's fixture data.
            await demo.Fence.RunAsync(demo.Actor.TenantId,async token=>{
                var result=await build(demo,token);await sessions.GetAsync(actor,id,token);
                context.Response.Headers.CacheControl="no-store";await Results.Ok(result).ExecuteAsync(context);return true;
            },context.RequestAborted);
        }
    }
}
