using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dse.Infrastructure;

public sealed record DemoWorkflowContext(string Id,ActorAccess Actor,IServiceProvider Services);

// Persistent business data, normal application services, existing real authority and
// tenant-wide AI ledger. No live inference is run by opening or resuming a workflow.
public sealed class DemoWorkflows(IServiceProvider root,IRecordStore shared,IConfiguration configuration,TimeProvider clock):IDisposable
{
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly Dictionary<string,(WorkflowRecordStore Store,ServiceProvider Services)> contexts=[];
    private const string StateId="workflow-state";
    private static string Id(ActorAccess a)=>"wf-"+ConversationService.Hash(string.Join('\n',a.TenantId,a.PrincipalId,a.PersonId,a.AccessEpoch,string.Join(',',a.Roles.Order(StringComparer.Ordinal)),string.Join(',',a.ScopeIds.Order(StringComparer.Ordinal))))[..48];
    private async Task<ActorAccess> Require(ActorAccess actor,CancellationToken ct)
    {
        actor=await new AuthorityProvider(new PrimaryRepository(shared),configuration).RequireAsync(actor,ct);
        if(!actor.HasRole("member")||!actor.HasRole("operator"))throw new DomainException(403,"FORBIDDEN");
        return actor;
    }
    private (WorkflowRecordStore Store,ServiceProvider Services) Context(ActorAccess actor,string id)
    {
        if(contexts.TryGetValue(id,out var cached))return cached;
        if(contexts.Count>=8)throw new DomainException(409,"WORKFLOW_CAPACITY");
        var store=new WorkflowRecordStore(shared,actor,id);
        var services=new ServiceCollection().AddSingleton(configuration).AddSingleton<IConfiguration>(configuration)
            .AddSingleton(root.GetRequiredService<ILoggerFactory>()).AddLogging()
            .AddCatalogServices(configuration).AddIngestion(configuration)
            .AddSingleton<IRecordStore>(store).AddSingleton<IProjectionRepository>(new MemoryProjectionRepository(store))
            .AddSingleton(root.GetRequiredService<AiLedger>()).AddSingleton<IAiTransport>(root.GetRequiredService<IAiTransport>())
            .AddSingleton<IIngestionBlobs>(new WorkflowIngestionBlobs(root.GetRequiredService<IIngestionBlobs>(),id))
            .AddSingleton<IIngestionQueue>(new WorkflowIngestionQueue(root.GetRequiredService<IIngestionQueue>(),id))
            .AddSingleton(new WorkerTrialCheckpoints(new PrimaryRepository(store),configuration,clock,id))
            .AddSingleton(this).AddSingleton(clock).BuildServiceProvider();
        var entry=(store,services);contexts.Add(id,entry);return entry;
    }
    public async Task<DemoWorkflowContext> StartAsync(ActorAccess actor,CancellationToken ct)
    {
        actor=await Require(actor,ct);var id=Id(actor);await gate.WaitAsync(ct);
        try
        {
            var entry=Context(actor,id);var p=RecordPartition.Catalog(actor.TenantId);var state=await entry.Store.ReadAsync(p,StateId,ct);
            if(state is null)
            {
                var value=RecordJson.New("demoWorkflow",StateId,actor.TenantId,actor.PrincipalId,actor.ScopeIds,clock.GetUtcNow());
                value["personId"]=actor.PersonId;value["workflowId"]=id;value["sourceTenantId"]=actor.TenantId;value["ownerEpoch"]=actor.AccessEpoch;value["state"]="initializing";
                try{await entry.Store.CommitAsync(p,[RecordWrite.Create(value)],ct);}catch(StoreConflictException){}
            }
            state=await entry.Store.ReadAsync(p,StateId,ct)??throw new DomainException(503,"WORKFLOW_UNAVAILABLE");
            var mapped=entry.Store.MapActor(actor);
            if(state.Value.Text("state")!="ready")
            {
                await DemoWorkflowSources.SeedAsync(entry.Store,entry.Services.GetRequiredService<IIngestionBlobs>(),entry.Services.GetRequiredService<SkillDictionaryService>(),mapped,clock,ct);
                state=await entry.Store.ReadAsync(p,StateId,ct)??throw new DomainException(503,"WORKFLOW_UNAVAILABLE");
                if(state.Value.Text("state")!="ready")
                {state.Value["state"]="ready";await entry.Store.CommitAsync(p,[RecordWrite.Replace(state.Value,state.ETag)],ct);}
            }
            return new(id,mapped,entry.Services);
        }
        finally{gate.Release();}
    }
    public async Task<DemoWorkflowContext> GetAsync(ActorAccess actor,string id,CancellationToken ct)
    {
        actor=await Require(actor,ct);if(id!=Id(actor))throw new DomainException(404,"NOT_FOUND");
        await gate.WaitAsync(ct);
        try
        {
            var entry=Context(actor,id);var state=await entry.Store.ReadAsync(RecordPartition.Catalog(actor.TenantId),StateId,ct);
            if(state?.Value.Text("state")!="ready")throw new DomainException(404,"WORKFLOW_NOT_FOUND");
            return new(id,entry.Store.MapActor(actor),entry.Services);
        }
        finally{gate.Release();}
    }
    internal async Task<DemoWorkflowContext> FromNoticeAsync(QueueNotice notice,CancellationToken ct)
    {
        var id=notice.WorkflowId;
        if(id is null||id.Length!=51||!id.StartsWith("wf-",StringComparison.Ordinal)||id[3..].Any(c=>!char.IsAsciiHexDigit(c)))throw new DomainException(404,"NOT_FOUND");
        var state=await shared.ReadAsync(RecordPartition.Catalog(id),StateId,ct);
        if(state?.Value.Text("kind")!="demoWorkflow"||state.Value.Text("state")!="ready"||state.Value.Text("sourceTenantId")!=notice.TenantId)throw new DomainException(404,"NOT_FOUND");
        var actor=await new AuthorityProvider(new PrimaryRepository(shared),configuration).PersonAsync(notice.TenantId,state.Value.Text("personId"),ct);
        if(actor is null||actor.PrincipalId!=state.Value.Text("ownerId")||actor.AccessEpoch!=state.Value.Text("ownerEpoch"))throw new DomainException(404,"NOT_FOUND");
        return await GetAsync(actor,id,ct);
    }
    public void Dispose(){foreach(var context in contexts.Values)context.Services.Dispose();gate.Dispose();}
}

public sealed class WorkflowIngestionBlobs(IIngestionBlobs blobs,string workflowId):IIngestionBlobs
{
    private string Key(string logical)=>workflowId+"/"+logical;
    public Task PutAsync(string container,string key,byte[] bytes,CancellationToken ct)=>blobs.PutAsync(container,Key(key),bytes,ct);
    public Task<byte[]> GetAsync(string container,string key,CancellationToken ct)=>blobs.GetAsync(container,Key(key),ct);
    public Task DeleteAsync(string container,string key,string etag,CancellationToken ct)=>blobs.DeleteAsync(container,Key(key),etag,ct);
    public async IAsyncEnumerable<BlobEntry> ListAsync(string container,string prefix,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct)
    {await foreach(var entry in blobs.ListAsync(container,Key(prefix),ct))yield return entry with{Key=entry.Key[(workflowId.Length+1)..]};}
}

public sealed class WorkflowIngestionQueue(IIngestionQueue queue,string workflowId):IIngestionQueue
{
    public Task SendAsync(QueueNotice notice,CancellationToken ct,bool poison=false)=>queue.SendAsync(notice with{WorkflowId=workflowId},ct,poison);
    public Task<QueueDelivery?> ReceiveAsync(TimeSpan visibility,CancellationToken ct)=>throw new InvalidOperationException("Only the root worker receives the common queue");
    public Task<QueueDelivery> RenewAsync(QueueDelivery delivery,TimeSpan visibility,CancellationToken ct)=>queue.RenewAsync(delivery,visibility,ct);
    public Task DeleteAsync(QueueDelivery delivery,CancellationToken ct)=>queue.DeleteAsync(delivery,ct);
}

internal static class DemoWorkflowSources
{
    public static async Task SeedAsync(IRecordStore store,IIngestionBlobs blobs,SkillDictionaryService dictionary,ActorAccess actor,TimeProvider clock,CancellationToken ct)
    {
        var p=RecordPartition.Catalog(actor.TenantId);
        if(await store.ReadAsync(p,actor.PersonId,ct) is null)
        {
            var person=RecordJson.New("person",actor.PersonId,actor.TenantId,actor.PrincipalId,actor.ScopeIds,clock.GetUtcNow());
            person["displayName"]="架空のデモ担当者";person["available"]=true;person["publicationState"]="draft";person["accessVersion"]=1;person["activeVersion"]=1;
            person["projectionIds"]=new JsonArray();person["documentHeads"]=new JsonObject();
            try{await store.CommitAsync(p,[RecordWrite.Create(person)],ct);}catch(StoreConflictException){}
        }
        foreach(var (name,text,hints) in new[]{
            ("technical-list-a","アプリ開発担当 ／ TypeScript、SQLite、Git",new[]{"TypeScript","SQLite","Git"}),
            ("technical-list-b","通信処理の開発担当 ／ Go、Linux、Git",new[]{"Go","Linux","Git"})})
        foreach(var distinct in new[]{false,true})
        {
            var id=name+(distinct?"-distinct":"");var fragmentId="fragment-"+id;
            if(await store.ReadAsync(p,fragmentId,ct) is null)
            {
                JsonObject Row(string kind,string key)=>RecordJson.New(kind,key,actor.TenantId,actor.PrincipalId,actor.ScopeIds,clock.GetUtcNow());
                var doc=Row("document","doc-"+id);doc["activeVersion"]=1;doc["accessVersion"]=1;doc["status"]="ready";doc["title"]=text;doc["personId"]=actor.PersonId;doc["documentType"]="report";doc["currentVersionId"]="ver-"+id;
                doc["historicalEvaluation"]=!distinct;
                var version=Row("documentVersion","ver-"+id);version["documentId"]=doc.Text("id");version["status"]="ready";
                var bytes=Original(text);var blobKey=actor.TenantId+"/fixtures/"+id+".docx";await blobs.PutAsync("originals",blobKey,bytes,ct);
                version["blobKey"]=blobKey;version["sha256"]=OfficeExtractor.Hash(bytes);version["mediaType"]=OfficeExtractor.WordMime;version["bytes"]=bytes.Length;version["processorVersion"]=IngestionOptions.ProcessorVersion;
                doc["extractionState"]="complete";doc["aiState"]="complete";doc["processingMode"]="saved_response";
                var evidence=Row("evidenceSpan","ev-"+id);evidence["documentId"]=doc.Text("id");evidence["documentVersionId"]=version.Text("id");evidence["validationState"]="validated";evidence["text"]=text;evidence["location"]=new JsonObject{["label"]="以前の架空原文"};
                var fragment=Row("documentFragment",fragmentId);fragment["documentId"]=doc.Text("id");fragment["description"]=text;fragment["skillHistoryOnly"]=false;fragment["workbookSectionKind"]="context";fragment["activeVersion"]=1;fragment["accessVersion"]=1;fragment["processingMode"]="saved_response";
                fragment["evidenceRefs"]=JsonSerializer.SerializeToNode(new[]{new EvidenceReference(evidence.Text("id"),version.Text("id"),1)},ProjectionRules.Json);
                fragment["specificSkills"]=JsonSerializer.SerializeToNode(new SpecificSkills(SpecificSkillRules.Version,"extracted",hints.Select(h=>new SpecificSkill(distinct?h:text,h,evidence.Text("id"),text){Basis="knowledge"}).ToArray()),ProjectionRules.Json);
                try{await store.CommitAsync(p,[RecordWrite.Create(doc),RecordWrite.Create(version),RecordWrite.Create(evidence),RecordWrite.Create(fragment)],ct);}catch(StoreConflictException){}
            }
            var source=await store.ReadAsync(p,fragmentId,ct)??throw new DomainException(503,"WORKFLOW_UNAVAILABLE");
            if(source.Value["skillHistoryOnly"]?.GetValue<bool>()!=true)
            {
                await dictionary.CollectAsync(actor,"workflow-seed-"+ConversationService.Hash(id)[..32],new(fragmentId),ct);
                if(!distinct){source=await store.ReadAsync(p,fragmentId,ct)??throw new DomainException(503,"WORKFLOW_UNAVAILABLE");source.Value["skillHistoryOnly"]=true;await store.CommitAsync(p,[RecordWrite.Replace(source.Value,source.ETag)],ct);}
            }
        }
    }
    private static byte[] Original(string text)
    {
        using var buffer=new MemoryStream();
        using(var zip=new System.IO.Compression.ZipArchive(buffer,System.IO.Compression.ZipArchiveMode.Create,true))
        {
            void Part(string name,string content){var entry=zip.CreateEntry(name);entry.LastWriteTime=new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero);using var writer=new StreamWriter(entry.Open());writer.Write(content);}
            Part("[Content_Types].xml","<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Part("_rels/.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Part("word/document.xml","<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>"+System.Security.SecurityElement.Escape(text)+"</w:t></w:r></w:p></w:body></w:document>");
        }
        return buffer.ToArray();
    }
}
