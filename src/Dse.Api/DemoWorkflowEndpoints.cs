using Dse.Domain;
using Dse.Infrastructure;

public static class DemoWorkflowEndpoints
{
    public const string Header="X-Dse-Workflow";
    public static void MapDemoWorkflows(this RouteGroupBuilder api)
    {
        api.MapPost("/demo-workflows/start",async(HttpContext c,DemoWorkflows workflows,CancellationToken ct)=>
        {
            if(c.Request.Headers.ContainsKey(Header))throw new DomainException(400,"WORKFLOW_ALREADY_SELECTED");
            var workflow=await workflows.StartAsync(RecordEndpoints.Actor(c),ct);c.Response.Headers.CacheControl="no-store";
            return Results.Ok(new{id=workflow.Id,persistence="durable",externalAiCalls=0});
        });
    }
    // Select storage after the real identity/authority checks, before normal handlers
    // resolve their services. Headers never grant roles or fall back to shared storage.
    public static async Task SelectAsync(HttpContext c,Func<Task> next)
    {
        if(!c.Request.Headers.TryGetValue(Header,out var header)){await next();return;}
        var path=c.Request.Path.Value??"";
        var prefixes=new[]{"/api/search","/api/v2/search","/api/v2/retrieval-runs","/api/v1/documents","/api/v1/upload-context","/api/v1/jobs","/api/v1/people","/api/v1/skill-dictionary","/api/v1/skill-candidates","/api/v1/workspace","/api/v1/challenges","/api/v1/sessions","/api/v1/conversation-context","/api/v1/knowledge"};
        if(!prefixes.Any(p=>path==p||path.StartsWith(p+"/",StringComparison.Ordinal)))throw new DomainException(403,"WORKFLOW_OPERATION_RESTRICTED");
        if(header.Count!=1)throw new DomainException(404,"NOT_FOUND");
        var original=c.RequestServices;var actor=RecordEndpoints.Actor(c);
        var workflow=await original.GetRequiredService<DemoWorkflows>().GetAsync(actor,header.ToString(),c.RequestAborted);
        c.Items[typeof(ActorAccess)]=workflow.Actor;c.RequestServices=new SelectedServices(workflow.Services,original);c.Response.Headers.CacheControl="no-store";
        try{await next();}finally{c.RequestServices=original;c.Items[typeof(ActorAccess)]=actor;}
    }
    private sealed class SelectedServices(IServiceProvider business,IServiceProvider host):IServiceProvider
    {public object? GetService(Type serviceType)=>business.GetService(serviceType)??host.GetService(serviceType);}
}
