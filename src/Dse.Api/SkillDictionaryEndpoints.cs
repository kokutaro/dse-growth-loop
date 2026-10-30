using Dse.Domain;
using Dse.Infrastructure;

public static class SkillDictionaryEndpoints
{
    public static void MapSkillDictionary(this RouteGroupBuilder api)
    {
        api.MapGet("/skill-dictionary",(int? version,HttpContext c,SkillDictionaryService s,AccessFence f,CancellationToken ct)=>
            new FencedResult(f,RecordEndpoints.Actor(c).TenantId,async t=>{c.Response.Headers.CacheControl="no-store";return Results.Ok(await s.ReadUnderFenceAsync(RecordEndpoints.Actor(c),version,t));},ct));
        api.MapGet("/skill-candidates",(string? continuationToken,HttpContext c,SkillDictionaryService s,AccessFence f,PageTokens tokens,CancellationToken ct)=>
            new FencedResult(f,RecordEndpoints.Actor(c).TenantId,async t=>{c.Response.Headers.CacheControl="no-store";var actor=RecordEndpoints.Actor(c);const string filter="skill-candidates-v1:50";var result=await s.ListUnderFenceAsync(actor,tokens.Decode(actor,filter,continuationToken),t);result["continuationToken"]=tokens.Encode(actor,filter,result["continuationToken"]?.GetValue<string>());return Results.Ok(result);},ct));
        api.MapGet("/skill-candidates/{id}/source",(string id,HttpContext c,SkillDictionaryService s,AccessFence f,CancellationToken ct)=>
            new FencedResult(f,RecordEndpoints.Actor(c).TenantId,async t=>{c.Response.Headers.CacheControl="no-store";return Results.Ok(await s.SourceUnderFenceAsync(RecordEndpoints.Actor(c),id,t));},ct));
        api.MapGet("/skill-candidates/{id}/history",(string id,HttpContext c,SkillDictionaryService s,AccessFence f,CancellationToken ct)=>
            new FencedResult(f,RecordEndpoints.Actor(c).TenantId,async t=>{c.Response.Headers.CacheControl="no-store";return Results.Ok(await s.HistoryUnderFenceAsync(RecordEndpoints.Actor(c),id,t));},ct));
        api.MapPost("/skill-dictionary/preview",(SkillDictionaryPreview input,HttpContext c,SkillDictionaryService s,AccessFence f,CancellationToken ct)=>
            new FencedResult(f,RecordEndpoints.Actor(c).TenantId,async t=>{c.Response.Headers.CacheControl="no-store";return Results.Ok(await s.PreviewUnderFenceAsync(RecordEndpoints.Actor(c),input,t));},ct));
        api.MapGet("/skill-candidates/{id}/suggestion",(string id,int version,HttpContext c,SkillDictionaryService s,AccessFence f,CancellationToken ct)=>
            new FencedResult(f,RecordEndpoints.Actor(c).TenantId,async t=>{c.Response.Headers.CacheControl="no-store";return Results.Ok(await s.ReadSuggestionUnderFenceAsync(RecordEndpoints.Actor(c),id,version,t));},ct));
        api.MapPost("/skill-candidates/{id}/suggest",async(string id,SkillSuggestionRequest input,HttpContext c,SkillDictionaryService s,CancellationToken ct)=>
        {c.Response.Headers.CacheControl="no-store";return Results.Ok(await s.SuggestAsync(RecordEndpoints.Actor(c),id,c.Request.Headers["Idempotency-Key"].ToString(),c.Request.Headers.IfMatch.ToString(),input,ct));});
        api.MapPost("/skill-candidates/collect",async(SkillCandidateCollect input,HttpContext c,SkillDictionaryService s,CancellationToken ct)=>
        {c.Response.Headers.CacheControl="no-store";var r=await s.CollectAsync(RecordEndpoints.Actor(c),c.Request.Headers["Idempotency-Key"].ToString(),input,ct);return Results.Json(r.Body,statusCode:r.Status);});
        api.MapPost("/skill-candidates/{id}/review",async(string id,SkillDictionaryReview input,HttpContext c,SkillDictionaryService s,CancellationToken ct)=>
        {c.Response.Headers.CacheControl="no-store";var r=await s.ReviewAsync(RecordEndpoints.Actor(c),id,c.Request.Headers["Idempotency-Key"].ToString(),c.Request.Headers.IfMatch.ToString(),input,ct);return Results.Json(r.Body,statusCode:r.Status);});
    }
}
