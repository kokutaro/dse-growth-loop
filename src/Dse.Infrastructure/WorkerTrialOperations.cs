using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dse.Infrastructure;

public static class WorkerTrialOperations
{
    public static byte[] FixtureBytes()
    {
        using var stream=typeof(WorkerTrialOperations).Assembly.GetManifestResourceStream("Dse.WorkerTrialSource")??throw new InvalidOperationException("Trial source unavailable");
        using var bytes=new MemoryStream();stream.CopyTo(bytes);return bytes.ToArray();
    }
    public static async Task<JsonObject> RunAsync(string mode,IConfiguration originalConfig,string? evidenceDirectory,CancellationToken ct)
    {
        if(mode is not ("inspect" or "plan" or "prepare" or "duplicate"))throw new ArgumentException("Unknown trial operation");
        if(mode is "prepare" or "duplicate" && (!OperatingSystem.IsLinux()||Environment.GetEnvironmentVariable("CONTAINER_APP_NAME")!="dse-worker"))throw new DomainException(403,"TRIAL_REMOTE_WORKER_REQUIRED");

        var config=new ConfigurationBuilder().AddConfiguration(originalConfig).AddInMemoryCollection(new Dictionary<string,string?>{
            ["Ai:Mode"]="Mock",["AiUsage:Stopped"]="true",["AiUsage:LiveOptIn"]="false"}).Build();
        var trial=config["Diagnostics:WorkerTrial:Id"]??"";var workflow=config["Diagnostics:WorkerTrial:WorkflowId"]??"";
        var id=config["Diagnostics:WorkerTrial:DocumentId"]??"";var personId=config["Diagnostics:WorkerTrial:PersonId"]??"";
        var tenant=config["Auth:TenantId"]??"";
        if(config["Data:Mode"]!="Azure"||!RecordJson.IsId(trial)||!trial.StartsWith("trial-worker-",StringComparison.Ordinal)||
            !RecordJson.IsId(id)||!id.StartsWith("doc-worker-trial-",StringComparison.Ordinal)||workflow.Length!=51||
            config["Diagnostics:WorkerTrial:SourceSha256"]!=WorkerTrialCheckpoints.FixtureSha256||!RecordJson.IsId(personId))throw new ArgumentException("Explicit bounded trial target required");
        if(mode=="prepare"&&config["Diagnostics:WorkerTrial:Enabled"]!="true")throw new ArgumentException("Verify the deployed trial settings, then explicitly enable the operator plan before preparation");

        var evidence=evidenceDirectory??Path.Combine(Path.GetTempPath(),"dse-worker-trial",trial);Directory.CreateDirectory(evidence);
        using var services=new ServiceCollection().AddSingleton<IConfiguration>(config).AddLogging().AddCatalog(config,false).AddIngestion(config)
            .AddSingleton<IAiTransport>(new NoInferenceTransport()).BuildServiceProvider();
        return await ExecuteWithServicesAsync(mode,config,services,evidence,ct);
    }
    // Internal seam for real common-service tests with in-memory adapters. Public
    // execution always validates the Azure target and remote mutation boundary first.
    internal static async Task<JsonObject> ExecuteWithServicesAsync(string mode,IConfiguration config,IServiceProvider services,string evidence,CancellationToken ct)
    {
        var trial=config["Diagnostics:WorkerTrial:Id"]!;var workflow=config["Diagnostics:WorkerTrial:WorkflowId"]!;
        var id=config["Diagnostics:WorkerTrial:DocumentId"]!;var personId=config["Diagnostics:WorkerTrial:PersonId"]!;
        var tenant=config["Auth:TenantId"]!;
        var root=services.GetRequiredService<IPrimaryRepository>();
        var actor=await services.GetRequiredService<AuthorityProvider>().PersonAsync(tenant,personId,ct)??throw new DomainException(403,"FORBIDDEN");
        if(!actor.HasRole("operator")||!actor.HasRole("member"))throw new DomainException(403,"FORBIDDEN");
        var context=await services.GetRequiredService<DemoWorkflows>().GetAsync(actor,workflow,ct);
        var repo=context.Services.GetRequiredService<IPrimaryRepository>();var store=context.Services.GetRequiredService<IRecordStore>();var p=RecordPartition.Catalog(tenant);
        var doc=await repo.ReadAsync(p,id,ct);
        if(mode is "prepare" or "plan")
        {
            if(doc is not null)throw new DomainException(409,"TRIAL_ALREADY_EXISTS_INSPECT_INSTEAD");
            var bytes=FixtureBytes();var extractor=new OfficeExtractor(new());
            extractor.Validate(bytes,"fictional.docx",OfficeExtractor.WordMime,ct);
            var extraction=extractor.Extract(bytes,"fictional.docx",OfficeExtractor.WordMime,ct);
            if(OfficeExtractor.Hash(bytes)!=WorkerTrialCheckpoints.FixtureSha256||extraction.Spans.Length!=1||extraction.Spans[0].Text!="通信処理の開発担当 ／ Go、Linux、Git")throw new InvalidOperationException("Fictional fixture mismatch");
            var person=(await repo.ReadAsync(p,personId,ct))!;AccessPolicy.RequireSelf(context.Actor,person.Value);
            if(person.Value.Text("publicationState")=="withdrawn"||!AccessPolicy.CanUse(context.Actor,person.Value))throw new DomainException(409,"SOURCE_REVOKED");
            var departmentSnapshot=await services.GetRequiredService<AccessFence>().RunReadOnlyAsync(tenant,async(reads,token)=>
            {
                var authority=new AuthorityProvider(reads,config);var current=await authority.RequireAsync(actor,token);
                var owner=(await reads.ReadAsync(p,personId,token))??throw new DomainException(404,"NOT_FOUND");AccessPolicy.RequireSelf(current,owner.Value);
                if(owner.Value.Text("publicationState")=="withdrawn"||SyntheticTestAccess.IsTest(owner.Value))throw new DomainException(409,"SOURCE_REVOKED");
                return await authority.DepartmentSnapshotAsync(tenant,personId,DateTimeOffset.UtcNow,token)??throw new DomainException(409,"TRIAL_DEPARTMENT_UNKNOWN");
            },ct);
            var departmentId=departmentSnapshot.Text("departmentId");if(!RecordJson.IsId(departmentId))throw new DomainException(409,"TRIAL_DEPARTMENT_UNKNOWN");
            var title="架空故障試験 "+id;
            var activity="activity-"+ConversationService.Hash(actor.PrincipalId+":"+title);
            if(await repo.ReadAsync(p,activity,ct) is not null)throw new DomainException(409,"TRIAL_ACTIVITY_EXISTS");
            string? cursor=null;
            do{var page=await repo.QueryAsync(p,"job",null,null,100,cursor,ct);if(page.Items.Any(r=>r.Value.Text("status") is "queued" or "processing"))throw new DomainException(409,"OTHER_WORKFLOW_JOB_ACTIVE");cursor=page.ContinuationToken;}while(cursor is not null);
            var held=await root.ReadAsync(p,"system-access-fence",ct);if(held?.Value.Text("holder") is {Length:>0})throw new DomainException(503,"ACCESS_BUSY");
            if(mode=="plan")
            {
                return new JsonObject{["readOnly"]=true,["documentId"]=id,["activityId"]=activity,["sourceSha256"]=WorkerTrialCheckpoints.FixtureSha256,["sourceSpans"]=1,["operatorAndPersonCurrent"]=true,["departmentSnapshot"]=departmentSnapshot.DeepClone(),["workflowJobsActive"]=false,["fenceHeld"]=false};
            }
            await ClaimOperationAsync(repo,context.Actor,workflow,trial,id,"prepare",ct);
            using(var once=new FileStream(Path.Combine(evidence,"prepare-once.lock"),FileMode.CreateNew,FileAccess.Write))await JsonSerializer.SerializeAsync(once,new{trial,workflow,id,at=DateTimeOffset.UtcNow},cancellationToken:ct);
            var now=DateTimeOffset.UtcNow;var value=RecordJson.New("document",id,tenant,actor.PrincipalId,person.Value.Strings("scopeIds"),now);
            value["title"]=title;value["documentType"]="report";value["personId"]=personId;value["departmentId"]=departmentId;
            value["accessVersion"]=1;value["extractionState"]="pending";value["aiState"]="not_started";value["currentVersionId"]=id+"-v1";
            value["workerTrial"]=new JsonObject{["id"]=trial,["armedAt"]=now,["expiresAt"]=now.AddMinutes(50)};
            var version=RecordJson.New("documentVersion",id+"-v1",tenant,actor.PrincipalId,value.Strings("scopeIds"),now);
            var blob=tenant+"/worker-trials/"+id+".docx";version["documentId"]=id;version["blobKey"]=blob;version["sha256"]=WorkerTrialCheckpoints.FixtureSha256;
            version["mediaType"]=OfficeExtractor.WordMime;version["bytes"]=bytes.Length;version["processorVersion"]=IngestionOptions.ProcessorVersion;
            // No importKey: this maintenance fixture never mutates the existing person's documentHeads.
            await context.Services.GetRequiredService<IIngestionBlobs>().PutAsync("originals",blob,bytes,ct);
            var accepted=await context.Services.GetRequiredService<DocumentAcceptance>().AcceptAsync(context.Actor,value,version,trial+"-accept",ct);
            using(var file=new FileStream(Path.Combine(evidence,"accepted.json"),FileMode.CreateNew,FileAccess.Write))await JsonSerializer.SerializeAsync(file,accepted,cancellationToken:ct);
            var jobId=accepted.Body!["jobId"]!.GetValue<string>();
            if(!await context.Services.GetRequiredService<JobNotifications>().TrySendAsync(tenant,jobId,ct))throw new InvalidOperationException("Accepted; notification pending. Inspect before any retry.");
            return new JsonObject{["trial"]=trial,["workflow"]=workflow,["documentId"]=id,["jobId"]=jobId,["inferenceCallsByThisTool"]=0};
        }
        if(doc is null){return new JsonObject{["documentExists"]=false};}
        var jobName=doc.Value.Text("enrichmentJobId");var job=jobName==""?null:await repo.ReadAsync(p,jobName,ct);
        if(mode=="duplicate")
        {
            var version=await repo.ReadAsync(p,doc.Value.Text("currentVersionId"),ct);
            var plan=doc.Value["workerTrial"] as JsonObject;
            if(job is null||job.Value.Text("aggregateId")!=id||job.Value.Number("aggregateVersion")!=1||job.Value.Text("jobType")!="enrichment"||
                job.Value.Text("supersedesJobId")!=""||job.Value.Text("status") is not ("queued" or "ready")||
                doc.Value.Text("ownerId")!=context.Actor.PrincipalId||job.Value.Text("ownerId")!=context.Actor.PrincipalId||
                doc.Value.Text("documentType")!="report"||doc.Value.Number("sourceVersion")!=1||doc.Value.Number("activeVersion")!=1||!AccessPolicy.CanUse(context.Actor,doc.Value)||
                plan?.Text("id")!=trial||plan["armedAt"]!.Deserialize<DateTimeOffset>()>DateTimeOffset.UtcNow||plan["expiresAt"]!.Deserialize<DateTimeOffset>()<=DateTimeOffset.UtcNow||
                plan["expiresAt"]!.Deserialize<DateTimeOffset>()-plan["armedAt"]!.Deserialize<DateTimeOffset>()>TimeSpan.FromHours(1)||
                version?.Value.Text("sha256")!=WorkerTrialCheckpoints.FixtureSha256||version.Value.Text("mediaType")!=OfficeExtractor.WordMime||
                version.Value.Text("documentId")!=id||version.Value.Number("sourceVersion")!=1||version.Value.Text("ownerId")!=context.Actor.PrincipalId||
                job.Value["workerTrialClaims"]?["built"]?["trialId"]?.GetValue<string>()!=trial)throw new DomainException(409,"TRIAL_NOT_READY");
            var held=await root.ReadAsync(p,"system-access-fence",ct);if(held?.Value.Text("holder") is {Length:>0})throw new DomainException(503,"ACCESS_BUSY");
            await ClaimOperationAsync(repo,context.Actor,workflow,trial,id,"duplicate",ct);
            using(var once=new FileStream(Path.Combine(evidence,"duplicates-once.lock"),FileMode.CreateNew,FileAccess.Write))await JsonSerializer.SerializeAsync(once,new{jobId=jobName,at=DateTimeOffset.UtcNow},cancellationToken:ct);
            var queue=context.Services.GetRequiredService<IIngestionQueue>();for(var i=0;i<2;i++)await queue.SendAsync(new(tenant,jobName,1),ct);
            return new JsonObject{["jobId"]=jobName,["duplicatesSent"]=2};
        }
        var runs=job is null?new RecordPage([],null):await repo.QueryAsync(p,"aiRun","jobId",jobName,100,null,ct);
        var receipts=job is null?new RecordPage([],null):await repo.QueryAsync(p,"aiResultReceipt","jobId",jobName,100,null,ct);
        var report=new JsonObject{["at"]=DateTimeOffset.UtcNow,["workflow"]=workflow,["documentId"]=id,["documentEtag"]=doc.ETag,["aiState"]=doc.Value["aiState"]?.DeepClone(),["job"]=job?.Value.Copy(),
            ["runs"]=new JsonArray(runs.Items.Select(r=>(JsonNode?)r.Value.Copy()).ToArray()),["receiptStates"]=new JsonArray(receipts.Items.Select(r=>(JsonNode?)new JsonObject{["id"]=r.Value.Text("id"),["state"]=r.Value.Text("state"),["expiresAt"]=r.Value["expiresAt"]?.DeepClone()}).ToArray())};
        using(var file=new FileStream(Path.Combine(evidence,"inspect-"+DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ")+".json"),FileMode.CreateNew,FileAccess.Write))await JsonSerializer.SerializeAsync(file,report,cancellationToken:ct);
        return new JsonObject{["at"]=report["at"]!.DeepClone(),["jobId"]=jobName,["status"]=job?.Value.Text("status"),["attempt"]=job?.Value.Number("attempt"),["lastError"]=job?.Value.Text("lastErrorCode"),
            ["availableAt"]=job?.Value["availableAt"]?.DeepClone(),["claims"]=job?.Value["workerTrialClaims"]?.DeepClone(),["providerAttempts"]=runs.Items.Sum(r=>(r.Value["trace"] as JsonObject)?.Number("attemptCount")??0),
            ["durableReuses"]=runs.Items.Count(r=>(r.Value["trace"] as JsonObject)?.Text("cacheKind")=="durable")};

    }
    private static Task<bool> ClaimOperationAsync(IPrimaryRepository repository,ActorAccess actor,string workflow,string trial,string document,string operation,CancellationToken ct)=>
        new AccessFence(repository).RunAsync(actor.TenantId,async token=>
        {
            actor=await new AuthorityProvider(repository).RequireAsync(actor,token);
            if(!actor.HasRole("operator"))throw new DomainException(403,"FORBIDDEN");
            var p=RecordPartition.Catalog(actor.TenantId);
            var id="worker-trial-operation-"+ConversationService.Hash(workflow+":"+trial+":"+document+":"+operation);
            if(await repository.ReadAsync(p,id,token) is not null)throw new DomainException(409,"TRIAL_OPERATION_CLAIMED_INSPECT_INSTEAD");
            var claim=RecordJson.New("idempotency",id,actor.TenantId,actor.PrincipalId,["system"],DateTimeOffset.UtcNow);
            claim["state"]="claimed";claim["operation"]=operation;claim["trialId"]=trial;claim["documentId"]=document;
            // No TTL and no timed re-arming. An unknown Blob/Queue outcome is
            // inspected, never made dispatchable by a restarted pod's empty /tmp.
            await repository.CommitAsync(p,[RecordWrite.Create(claim)],token);return true;
        },ct);
    sealed class NoInferenceTransport:IAiTransport
    {
        public Task<AiWireResponse> SendAsync(AiModel model,JsonObject body,int maxResponseBytes,CancellationToken ct)=>
            throw new InvalidOperationException("Operator tooling never dispatches inference");
    }
}
