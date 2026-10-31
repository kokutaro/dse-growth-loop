using Dse.Domain;
using Dse.Infrastructure;

public static class SyntheticTestEndpoints
{
    public sealed record Withdrawal(string Reason);
    public static void MapSyntheticTests(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/test-people",(HttpContext c,SyntheticTestProfiles s,AccessFence f,CancellationToken ct)=>
            new FencedResult(f,RecordEndpoints.Actor(c).TenantId,async t=>{c.Response.Headers.CacheControl="no-store";return Results.Ok(await s.ListUnderFenceAsync(RecordEndpoints.Actor(c),t));},ct));
        api.MapPost("/admin/test-people",async(SyntheticTestRegistration input,HttpContext c,SyntheticTestProfiles s,CancellationToken ct)=>
        {
            var r=await s.RegisterAsync(RecordEndpoints.Actor(c),input,c.Request.Headers["Idempotency-Key"].ToString(),ct);
            c.Response.Headers.CacheControl="no-store";c.Response.Headers.Location=r.Location;c.Response.Headers.ETag=r.ETag;
            return Results.Json(r.Body,statusCode:r.Status);
        });
        api.MapPost("/admin/test-people/{id}/withdraw",async(string id,Withdrawal input,HttpContext c,SyntheticTestProfiles s,CancellationToken ct)=>
        {
            var r=await s.WithdrawAsync(RecordEndpoints.Actor(c),id,c.Request.Headers["Idempotency-Key"].ToString(),c.Request.Headers.IfMatch.ToString(),input.Reason,ct);
            c.Response.Headers.CacheControl="no-store";c.Response.Headers.ETag=r.ETag;return Results.Json(r.Body,statusCode:r.Status);
        });
        api.MapPost("/admin/test-people/{id}/documents",async(string id,HttpContext c,SyntheticTestProfiles s,DocumentUploads uploads,IngestionOptions limits,CancellationToken ct)=>
        {
            var actor=await s.OperatorAsync(RecordEndpoints.Actor(c),ct);
            if(!c.Request.HasFormContentType)throw new DomainException(400,"INVALID_INPUT");
            IFormCollection form;
            try{form=await c.Request.ReadFormAsync(ct);}catch(InvalidDataException){throw new DomainException(400,"INVALID_INPUT");}
            if(form.Files.Count!=1||form.Files[0].Name!="file"||form.Keys.Count()!=1||form["syntheticDeclaration"]!="true")throw new DomainException(400,"INVALID_INPUT");
            var file=form.Files[0];if(file.Length>limits.OriginalBytes)throw new DomainException(413,"PAYLOAD_TOO_LARGE");
            using var bytes=new MemoryStream();await file.CopyToAsync(bytes,ct);
            var r=await uploads.UploadTestAsync(actor,id,file.FileName,file.ContentType,bytes.ToArray(),c.Request.Headers["Idempotency-Key"].ToString(),ct);
            c.Response.Headers.CacheControl="no-store";c.Response.Headers.Location=r.Location;return Results.Json(r.Body,statusCode:r.Status);
        }).RequireRateLimiting("upload");
        api.MapPost("/admin/test-people/search",async(SearchRequest request,HttpContext c,SyntheticTestProfiles s,ICatalog catalog,CancellationToken ct)=>
        {
            var actor=await s.OperatorAsync(RecordEndpoints.Actor(c),ct);c.Response.Headers.CacheControl="no-store";
            return Results.Ok(await catalog.SearchAsync(request with{SyntheticOnly=true},actor,ct));
        }).RequireRateLimiting("search");
    }
}
