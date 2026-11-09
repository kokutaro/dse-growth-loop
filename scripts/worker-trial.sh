#!/usr/bin/env bash
set -euo pipefail
if [[ ${1:-} == --help ]]; then
  echo 'worker-trial.sh inspect|prepare|duplicate CONFIG_JSON EVIDENCE_DIR'
  echo 'Uses existing Azure identity. prepare accepts only the repository fictional Word fixture; never infers or changes IAM.'
  exit 0
fi
[[ $# == 3 && ( $1 == inspect || $1 == prepare || $1 == duplicate ) ]]
trial_mode=$1
trial_config=$(cd "$(dirname "$2")" && pwd)/$(basename "$2")
trial_evidence=$(cd "$3" && pwd)
trial_repository=$(cd "$(dirname "$0")/.." && pwd)
cd "$trial_repository"
{
printf '#:property PublishAot=false\n#:property JsonSerializerIsReflectionEnabledByDefault=true\n'
printf '#:project %s/src/Dse.Infrastructure/Dse.Infrastructure.csproj\n' "$trial_repository"
cat <<'CS'
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var mode=args[0];var evidence=args[2];
var config=new ConfigurationBuilder().AddJsonFile(args[1]).AddInMemoryCollection(new Dictionary<string,string?>{
    ["Ai:Mode"]="Mock",["AiUsage:Stopped"]="true",["AiUsage:LiveOptIn"]="false"}).Build();
var trial=config["Diagnostics:WorkerTrial:Id"]??"";var workflow=config["Diagnostics:WorkerTrial:WorkflowId"]??"";
var id=config["Diagnostics:WorkerTrial:DocumentId"]??"";var personId=config["Diagnostics:WorkerTrial:PersonId"]??"";
var tenant=config["Auth:TenantId"]??"";
if(config["Data:Mode"]!="Azure"||!RecordJson.IsId(trial)||!trial.StartsWith("trial-worker-",StringComparison.Ordinal)||
    !RecordJson.IsId(id)||!id.StartsWith("doc-worker-trial-",StringComparison.Ordinal)||workflow.Length!=51||
    config["Diagnostics:WorkerTrial:SourceSha256"]!=WorkerTrialCheckpoints.FixtureSha256||!RecordJson.IsId(personId))throw new ArgumentException("Explicit bounded trial target required");
using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));var ct=deadline.Token;
using var services=new ServiceCollection().AddSingleton<IConfiguration>(config).AddLogging().AddCatalog(config,false).AddIngestion(config).BuildServiceProvider();
var root=services.GetRequiredService<IPrimaryRepository>();
var actor=await services.GetRequiredService<AuthorityProvider>().PersonAsync(tenant,personId,ct)??throw new DomainException(403,"FORBIDDEN");
if(!actor.HasRole("operator")||!actor.HasRole("member"))throw new DomainException(403,"FORBIDDEN");
var context=await services.GetRequiredService<DemoWorkflows>().GetAsync(actor,workflow,ct);
var repo=context.Services.GetRequiredService<IPrimaryRepository>();var store=context.Services.GetRequiredService<IRecordStore>();var p=RecordPartition.Catalog(tenant);
var doc=await repo.ReadAsync(p,id,ct);
if(mode=="prepare")
{
    if(doc is not null)throw new DomainException(409,"TRIAL_ALREADY_EXISTS_INSPECT_INSTEAD");
    var source=Path.Combine(Directory.GetCurrentDirectory(),"tests/Dse.Tests/Fixtures/worker-trial-source.docx");
    var bytes=await File.ReadAllBytesAsync(source,ct);var extractor=new OfficeExtractor(new());
    extractor.Validate(bytes,"fictional.docx",OfficeExtractor.WordMime,ct);
    var extraction=extractor.Extract(bytes,"fictional.docx",OfficeExtractor.WordMime,ct);
    if(OfficeExtractor.Hash(bytes)!=WorkerTrialCheckpoints.FixtureSha256||extraction.Spans.Length!=1||extraction.Spans[0].Text!="通信処理の開発担当 ／ Go、Linux、Git")throw new InvalidOperationException("Fictional fixture mismatch");
    var person=(await repo.ReadAsync(p,personId,ct))!;AccessPolicy.RequireSelf(context.Actor,person.Value);
    if(person.Value.Text("publicationState")=="withdrawn"||!AccessPolicy.CanUse(context.Actor,person.Value))throw new DomainException(409,"SOURCE_REVOKED");
    var title="架空故障試験 "+id;
    var activity="activity-"+ConversationService.Hash(actor.PrincipalId+":"+title);
    if(await repo.ReadAsync(p,activity,ct) is not null)throw new DomainException(409,"TRIAL_ACTIVITY_EXISTS");
    string? cursor=null;
    do{var page=await repo.QueryAsync(p,"job",null,null,100,cursor,ct);if(page.Items.Any(r=>r.Value.Text("status") is "queued" or "processing"))throw new DomainException(409,"OTHER_WORKFLOW_JOB_ACTIVE");cursor=page.ContinuationToken;}while(cursor is not null);
    var held=await root.ReadAsync(p,"system-access-fence",ct);if(held?.Value.Text("holder") is {Length:>0})throw new DomainException(503,"ACCESS_BUSY");
    using(var once=new FileStream(Path.Combine(evidence,"prepare-once.lock"),FileMode.CreateNew,FileAccess.Write))await JsonSerializer.SerializeAsync(once,new{trial,workflow,id,at=DateTimeOffset.UtcNow},cancellationToken:ct);
    var now=DateTimeOffset.UtcNow;var value=RecordJson.New("document",id,tenant,actor.PrincipalId,person.Value.Strings("scopeIds"),now);
    value["title"]=title;value["documentType"]="report";value["personId"]=personId;value["departmentId"]=person.Value.Text("departmentId");
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
    Console.WriteLine(new JsonObject{["trial"]=trial,["workflow"]=workflow,["documentId"]=id,["jobId"]=jobId,["inferenceCallsByThisTool"]=0}.ToJsonString());return;
}
if(doc is null){Console.WriteLine(new JsonObject{["documentExists"]=false}.ToJsonString());return;}
var jobName=doc.Value.Text("enrichmentJobId");var job=jobName==""?null:await repo.ReadAsync(p,jobName,ct);
if(mode=="duplicate")
{
    if(job is null||job.Value.Text("aggregateId")!=id||job.Value.Number("aggregateVersion")!=1||job.Value.Text("jobType")!="enrichment")throw new DomainException(409,"TRIAL_NOT_READY");
    var held=await root.ReadAsync(p,"system-access-fence",ct);if(held?.Value.Text("holder") is {Length:>0})throw new DomainException(503,"ACCESS_BUSY");
    using(var once=new FileStream(Path.Combine(evidence,"duplicates-once.lock"),FileMode.CreateNew,FileAccess.Write))await JsonSerializer.SerializeAsync(once,new{jobId=jobName,at=DateTimeOffset.UtcNow},cancellationToken:ct);
    var queue=context.Services.GetRequiredService<IIngestionQueue>();for(var i=0;i<2;i++)await queue.SendAsync(new(tenant,jobName,1),ct);
    Console.WriteLine(new{jobId=jobName,duplicatesSent=2});return;
}
var runs=job is null?new RecordPage([],null):await repo.QueryAsync(p,"aiRun","jobId",jobName,100,null,ct);
var receipts=job is null?new RecordPage([],null):await repo.QueryAsync(p,"aiResultReceipt","jobId",jobName,100,null,ct);
var report=new JsonObject{["at"]=DateTimeOffset.UtcNow,["workflow"]=workflow,["documentId"]=id,["documentEtag"]=doc.ETag,["aiState"]=doc.Value["aiState"]?.DeepClone(),["job"]=job?.Value.Copy(),
    ["runs"]=new JsonArray(runs.Items.Select(r=>(JsonNode?)r.Value.Copy()).ToArray()),["receiptStates"]=new JsonArray(receipts.Items.Select(r=>(JsonNode?)new JsonObject{["id"]=r.Value.Text("id"),["state"]=r.Value.Text("state"),["expiresAt"]=r.Value["expiresAt"]?.DeepClone()}).ToArray())};
using(var file=new FileStream(Path.Combine(evidence,"inspect-"+DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ")+".json"),FileMode.CreateNew,FileAccess.Write))await JsonSerializer.SerializeAsync(file,report,cancellationToken:ct);
Console.WriteLine(new JsonObject{["at"]=report["at"]!.DeepClone(),["jobId"]=jobName,["status"]=job?.Value.Text("status"),["attempt"]=job?.Value.Number("attempt"),["lastError"]=job?.Value.Text("lastErrorCode"),
    ["availableAt"]=job?.Value["availableAt"]?.DeepClone(),["claims"]=job?.Value["workerTrialClaims"]?.DeepClone(),["providerAttempts"]=runs.Items.Sum(r=>(r.Value["trace"] as JsonObject)?.Number("attemptCount")??0),
    ["durableReuses"]=runs.Items.Count(r=>(r.Value["trace"] as JsonObject)?.Text("cacheKind")=="durable")}.ToJsonString());
CS
} | dotnet run - -- "$trial_mode" "$trial_config" "$trial_evidence"
