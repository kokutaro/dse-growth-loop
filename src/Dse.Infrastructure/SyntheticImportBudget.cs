using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// Separate immutable allowance: changing it never invalidates the source document's pinned ETag.
// A failed/unknown send consumes its slot. Retrying a Job cannot refill this allowance.
public static class SyntheticImportBudget
{
    public static string Id(string documentId) => "test-budget-" + OfficeExtractor.Hash(Encoding.UTF8.GetBytes(documentId));
    public static JsonObject Configuration(IConfiguration? config) => Snapshot(config);
    private static JsonObject Snapshot(IConfiguration? config)
    {
        try { return SnapshotCore(config); }
        catch(Exception e) when(e is InvalidOperationException or FormatException or ArgumentException) { throw new DomainException(409,"TEST_BUDGET_CONFIGURATION_INVALID"); }
    }
    private static JsonObject SnapshotCore(IConfiguration? config)
    {
        if(config is null || config["Ai:Mode"]=="Mock")return new(){["mode"]="Mock"};
        var limits=AiLimits.Read(config);
        if(limits.Attempts!=1)throw new DomainException(409,"TEST_SINGLE_ATTEMPT_REQUIRED");
        var foundry=AiModel.Read(config,"Foundry",limits.OutputTokens);var jev=AiModel.Read(config,"Jev",limits.OutputTokens);
        // Pin provider/version/rates and call bounds, without storing credentials.
        return new(){["budgetVersion"]="workbook-import-v1",["inputTokens"]=limits.InputTokens,["outputTokens"]=limits.OutputTokens,
            ["foundry"]=JsonSerializer.SerializeToNode(foundry,ProjectionRules.Json),["jev"]=JsonSerializer.SerializeToNode(jev,ProjectionRules.Json),
            ["maximumUsd"]=(4m*(limits.InputTokens*foundry.InputUsdPerMillion+limits.OutputTokens*foundry.OutputUsdPerMillion)+
                3m*(limits.InputTokens*jev.InputUsdPerMillion+limits.OutputTokens*jev.OutputUsdPerMillion))/1000000m};
    }
    public static JsonObject Describe(IConfiguration config)
    {
        try
        {
            var snapshot=Snapshot(config);
            return new(){["maxCalls"]=7,["foundryCalls"]=4,["jevCalls"]=3,["structureCalls"]=1,["maximumSections"]=3,["maximumProjects"]=2,["mode"]=config["Ai:Mode"],
                ["inputTokensPerCall"]=snapshot["inputTokens"]?.DeepClone(),["outputTokensPerCall"]=snapshot["outputTokens"]?.DeepClone(),
                ["maximumUsd"]=snapshot["maximumUsd"]?.DeepClone(),["billingVerified"]=false};
        }
        catch(DomainException) { return new(){["available"]=false,["reason"]="TEST_BUDGET_CONFIGURATION_INVALID"}; }
    }
    public static JsonObject Create(JsonObject document,IConfiguration? config,DateTimeOffset now)
    {
        var row=RecordJson.New("syntheticImportBudget",Id(document.Text("id")),document.Text("tenantId"),document.Text("ownerId"),document.Strings("scopeIds"),now);
        SyntheticTestAccess.Mark(row,document);row["documentId"]=document.Text("id");row["configuration"]=Snapshot(config);row["dispatches"]=new JsonObject();return row;
    }
    // Caller holds the same access fence as the ordinary AiService/ledger.
    public static async Task ReserveAsync(IPrimaryRepository repository,IConfiguration config,ActorAccess actor,
        IReadOnlyCollection<string> documentIds,string operation,string prompt,string runId,int attempt,CancellationToken ct)
    {
        if(documentIds.Count==0)return;
        if(documentIds.Count!=1 || attempt!=1 || !(operation=="extractSkills"&&prompt==WorkbookRules.CellSkillPrompt || operation=="structureWorkbook"&&prompt==WorkbookRules.Version || operation=="decide"&&prompt==ProfilePipeline.PromptVersion))
            throw new AiException(AiFailure.Budget);
        var documentId=documentIds.Single();var p=RecordPartition.Catalog(actor.TenantId);
        var doc=(await repository.ReadAsync(p,documentId,ct))?.Value??throw new DomainException(409,"SOURCE_REVOKED");
        await SyntheticTestAccess.RequireLinkedAsync(repository,actor,doc,ct);
        var row=await repository.ReadAsync(p,Id(documentId),ct)??throw new AiException(AiFailure.Budget);
        if(!JsonNode.DeepEquals(row.Value["configuration"],Snapshot(config)))throw new AiException(AiFailure.Configuration);
        var dispatches=row.Value["dispatches"]!.AsObject();
        var key=runId+":"+attempt;
        // An existing dispatch is uncertain/consumed, never permission to send again.
        if(dispatches.ContainsKey(key) || dispatches.Count(x=>x.Value!.GetValue<string>()==operation)>=(operation=="structureWorkbook"?1:3))throw new AiException(AiFailure.Budget);
        dispatches[key]=operation;
        await repository.CommitAsync(p,[RecordWrite.Replace(row.Value,row.ETag)],ct);
    }
}
