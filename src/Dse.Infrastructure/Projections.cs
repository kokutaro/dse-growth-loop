using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dse.Infrastructure;

public interface IProjectionRepository
{
    Task<SearchUnit[]> FindAsync(SearchRequest request, SearchAccess access, CancellationToken ct);
    // Read public knowledge lazily; the consumer caps results after current source/consent rechecks.
    IAsyncEnumerable<SearchUnit[]> BrowseKnowledgePagesAsync(SearchRequest request, SearchAccess access, CancellationToken ct);
    Task SaveAsync(JsonObject projection, CancellationToken ct);
}
public sealed class MemoryProjectionRepository(IRecordStore store) : IProjectionRepository
{
    public async IAsyncEnumerable<SearchUnit[]> BrowseKnowledgePagesAsync(SearchRequest request, SearchAccess access, [EnumeratorCancellation] CancellationToken ct)
    {
        var terms = request.Terms.Select(Retriever.Normalize).ToHashSet(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var page = await store.QueryAsync(RecordPartition.Catalog(access.TenantId), "searchUnit", "entityType", "knowledge", 100, token, ct);
            yield return page.Items.Select(x => x.Value.Deserialize<SearchUnit>(ProjectionRules.Json)!)
                .Where(u => terms.Count == 0 || u.NormalizedTerms.Any(terms.Contains)).ToArray();
            token = page.ContinuationToken;
        } while (token is not null);
    }
    public async Task<SearchUnit[]> FindAsync(SearchRequest request, SearchAccess access, CancellationToken ct)
    {
        List<SearchUnit> units = [];
        string? token = null;
        do
        {
            var page = await store.QueryAsync(RecordPartition.Catalog(access.TenantId), "searchUnit", null, null, 100, token, ct);
            units.AddRange(page.Items.Select(x => x.Value.Deserialize<SearchUnit>(ProjectionRules.Json)!));
            token = page.ContinuationToken;
            ProjectionRules.CheckBudget(units.Count, 0);
        } while (token is not null);
        return units.ToArray();
    }
    public Task SaveAsync(JsonObject projection, CancellationToken ct) => ProjectionRules.SaveAsync(store, projection, ct);
}
public sealed class CosmosProjectionRepository(CosmosClient client, IConfiguration config, IRecordStore store, ILogger<CosmosProjectionRepository> logger) : IProjectionRepository
{
    public Task<SearchUnit[]> FindAsync(SearchRequest request, SearchAccess access, CancellationToken ct) => ReadAsync(CosmosCatalog.BuildQuery(request, access), access, ct);
    public IAsyncEnumerable<SearchUnit[]> BrowseKnowledgePagesAsync(SearchRequest request, SearchAccess access, CancellationToken ct) =>
        ReadPagesAsync(CosmosCatalog.BuildKnowledgeBrowseQuery(access, request.Terms), access, ct);
    private async Task<SearchUnit[]> ReadAsync(QueryDefinition query, SearchAccess access, CancellationToken ct)
    {
        List<SearchUnit> units = [];
        await foreach (var page in ReadPagesAsync(query, access, ct))
        {
            units.AddRange(page);
            ProjectionRules.CheckBudget(units.Count, 0);
        }
        return units.ToArray();
    }
    private async IAsyncEnumerable<SearchUnit[]> ReadPagesAsync(QueryDefinition query, SearchAccess access, [EnumeratorCancellation] CancellationToken ct)
    {
        var container = client.GetContainer(config["Cosmos:Database"] ?? "dse-growth-loop", "catalog");
        using var iterator = container.GetItemQueryIterator<SearchUnit>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(access.TenantId), MaxItemCount = 100 });
        double ru = 0;
        while (iterator.HasMoreResults)
        {
            SearchUnit[] values;
            using (var operation = OperationMeasurements.Start(MeasurementKind.ProjectionPage, cosmosSdk: true))
            {
                try
                {
                    var start = Stopwatch.GetTimestamp();
                    var page = await iterator.ReadNextAsync(ct);
                    operation.ObserveResponse(page.RequestCharge, 200);
                    ru += page.RequestCharge;
                    SearchBudget.Charge(page.RequestCharge, "projection_page");
                    logger.LogInformation("Cosmos search RequestCharge={RequestCharge} CorrelationId={CorrelationId} DurationMs={DurationMs}",
                        page.RequestCharge, Activity.Current?.TraceId.ToString() ?? "background", Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    ProjectionRules.CheckBudget(0, ru);
                    values = page.ToArray();
                }
                catch (Exception ex) { operation.Fail(ex); throw; }
            }
            yield return values;
        }
    }
    public Task SaveAsync(JsonObject projection, CancellationToken ct) => ProjectionRules.SaveAsync(store, projection, ct);
}
public static class ProjectionRules
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static void CheckBudget(int count, double ru)
    {
        if (count > 5000 || ru > 1000) throw new DomainException(503, "SEARCH_LIMIT_EXCEEDED");
    }
    internal static Task SaveAsync(IRecordStore store, JsonObject projection, CancellationToken ct)
    {
        if (projection.Text("kind") != "searchUnit" || projection.Number("projectionVersion") < 1 ||
            projection.Number("sourceVersion") < 1 || projection.Text("projectionState") is not ("building" or "ready"))
            throw new ArgumentException("Invalid projection");
        // Versioned IDs are immutable. Old events may create old rows, but cannot overwrite current rows.
        return store.CommitAsync(RecordPartition.Catalog(projection.Text("tenantId")), [RecordWrite.Create(projection)], ct);
    }
}

public sealed record SourceSnapshot(string Id, int SourceVersion, int ActiveVersion, int AccessVersion);
public sealed record ValidatedSource(SearchUnit Unit, SourceSnapshot[] Snapshots);

public sealed class SourceGuard(IPrimaryRepository repository, IConfiguration? configuration = null)
{
    public async Task<SearchUnit[]> FilterAsync(ActorAccess actor, IEnumerable<SearchUnit> units, CancellationToken ct)
    {
        List<SearchUnit> result = [];
        foreach (var unit in units)
        {
            var source = await ValidateAsync(actor, unit, ct);
            if (source is not null) result.Add(source.Unit);
        }
        return result.ToArray();
    }
    // Caller must hold the tenant access fence. This read set lives only for this
    // filtering phase; validation after AI always starts a new read set.
    public Task<SearchUnit[]> FilterUnderFenceAsync(ActorAccess actor, IEnumerable<SearchUnit> units, CancellationToken ct) =>
        new SourceGuard(new PhaseReads(repository), configuration).FilterAsync(actor, units, ct);

    private sealed class PhaseReads(IPrimaryRepository inner) : IPrimaryRepository
    {
        private readonly Dictionary<(RecordPartition, string), StoredRecord?> reads = [];
        public async Task<StoredRecord?> ReadAsync(RecordPartition partition, string id, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!reads.TryGetValue((partition, id), out var record))
                reads[(partition, id)] = record = await inner.ReadAsync(partition, id, ct);
            return record is null ? null : new(record.Value.Copy(), record.ETag);
        }
        public Task<RecordPage> QueryAsync(RecordPartition partition, string kind, string? field, string? parent, int size, string? token, CancellationToken ct) =>
            inner.QueryAsync(partition, kind, field, parent, size, token, ct);
        public Task CommitAsync(RecordPartition partition, IReadOnlyList<RecordWrite> writes, CancellationToken ct) =>
            throw new InvalidOperationException("Source validation is read-only");
    }
    public async Task<ValidatedSource> RequireAsync(ActorAccess actor, SearchUnit unit, CancellationToken ct) =>
        await ValidateAsync(actor, unit, ct) ?? throw new DomainException(409, "SOURCE_REVOKED");

    private async Task<JsonObject?> CurrentKnowledgeAuthorityAsync(ActorAccess actor, JsonObject source, int version, CancellationToken ct)
    {
        // An adapter without the current identity configuration cannot authorize a published conversation.
        if (configuration is null) return null;
        var authority = await KnowledgeService.PublishedAsync(repository, actor.TenantId, source.Text("sessionId"), version, ct);
        return authority is not null && await KnowledgeService.CurrentConsentParticipantsAsync(repository,
            configuration, actor.TenantId, authority, false, ct) is not null ? authority : null;
    }

    private async Task<ValidatedSource?> ValidateAsync(ActorAccess actor, SearchUnit unit, CancellationToken ct)
    {
        if (unit.TenantId != actor.TenantId || unit.Status != "active" || !unit.Available ||
            unit.TaxonomyVersion != Retriever.TaxonomyVersion || unit.ProjectionState != "ready" || unit.SourceVersion is null ||
            unit.AccessVersion is null || unit.ProjectionVersion != unit.SourceVersion || unit.EvidenceRefs is not { Length: > 0 } ||
            !unit.ScopeIds.Intersect(actor.ScopeIds).Any()) return null;
        var partition = RecordPartition.Catalog(actor.TenantId);
        var source = (await repository.ReadAsync(partition, unit.EntityId, ct))?.Value;
        if (source is null || source.Text("kind") != unit.EntityType || !AccessPolicy.CanUse(actor, source) ||
            source.Number("activeVersion") != unit.SourceVersion || source.Number("accessVersion") != unit.AccessVersion) return null;
        if (source.Text("kind") == "knowledge" && source.Text("contractVersion") == "knowledge-v1" &&
            await CurrentKnowledgeAuthorityAsync(actor, source, unit.SourceVersion.Value, ct) is null) return null;
        List<SourceSnapshot> snapshots = [Snapshot(source)];
        List<SourceRef> refs = [];
        foreach (var reference in unit.EvidenceRefs)
        {
            var evidence = await EvidenceAsync(actor, reference, ct);
            if (evidence is null) return null;
            refs.Add(new(reference.Id, evidence.Value.Document.Text("title"), evidence.Value.Span["location"]!["label"]!.GetValue<string>(), evidence.Value.Span.Text("text")));
            snapshots.Add(Snapshot(evidence.Value.Document));
        }
        return new(unit with { SourceRefs = refs.ToArray() }, snapshots.Distinct().ToArray());
    }
    public async Task<(JsonObject Span, JsonObject Document)?> EvidenceAsync(ActorAccess actor, EvidenceReference reference, CancellationToken ct)
    {
        var partition = RecordPartition.Catalog(actor.TenantId);
        var span = (await repository.ReadAsync(partition, reference.Id, ct))?.Value;
        if (span?.Text("kind") == "knowledge" && span.Text("contractVersion") == "knowledge-v1")
        {
            var authority = await CurrentKnowledgeAuthorityAsync(actor, span, reference.SourceVersion, ct);
            if (authority is null || reference.DocumentVersionId != reference.Id || !AccessPolicy.CanUse(actor, span) || span.Number("activeVersion") != reference.SourceVersion ||
                authority.Number("accessVersion") != span.Number("accessVersion")) return null;
            var evidence = new JsonObject { ["text"] = span.Text("summary"), ["location"] = new JsonObject { ["label"] = "承認済み要約" } };
            return (evidence, span);
        }
        var version = (await repository.ReadAsync(partition, reference.DocumentVersionId, ct))?.Value;
        if (span is null || version is null || span.Text("kind") != "evidenceSpan" || version.Text("kind") != "documentVersion" ||
            span.Text("validationState") != "validated" || version.Text("status") != "ready" ||
            span.Text("documentVersionId") != reference.DocumentVersionId || span.Text("documentId") != version.Text("documentId") ||
            span.Number("sourceVersion") != reference.SourceVersion || version.Number("sourceVersion") != reference.SourceVersion ||
            !AccessPolicy.CanRead(actor, span) || !AccessPolicy.CanRead(actor, version)) return null;
        var document = (await repository.ReadAsync(partition, version.Text("documentId"), ct))?.Value;
        return document is not null && document.Text("kind") == "document" && AccessPolicy.CanUse(actor, document) &&
            document.Number("activeVersion") == reference.SourceVersion ? (span, document) : null;
    }
    public async Task RequireSnapshotsAsync(ActorAccess actor, IEnumerable<SourceSnapshot> snapshots, CancellationToken ct)
    {
        foreach (var snapshot in snapshots)
        {
            var current = (await repository.ReadAsync(RecordPartition.Catalog(actor.TenantId), snapshot.Id, ct))?.Value;
            if (current is null || !AccessPolicy.CanUse(actor, current) || Snapshot(current) != snapshot ||
                current.Text("kind") == "knowledge" && current.Text("contractVersion") == "knowledge-v1" &&
                await CurrentKnowledgeAuthorityAsync(actor, current, snapshot.ActiveVersion, ct) is null)
                throw new DomainException(409, "SOURCE_REVOKED");
        }
    }
    private static SourceSnapshot Snapshot(JsonObject value) => new(value.Text("id"), value.Number("sourceVersion"), value.Number("activeVersion"), value.Number("accessVersion"));
}
