using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

public sealed record PipelinePlan(string Tenant, Dictionary<string, string?> Expected, RecordWrite[] Writes, string ResultId);

// Bounded atomic publication: no row becomes searchable until the complete generation commits.
// Oversized inputs are held for review rather than silently truncating their experiences.
public sealed class ProfilePipeline(IPrimaryRepository repository, IRecordStore store, AccessFence fence, ITextStructurer structurer,
    IDecisionProvider decisions, IdentityResolver identities, IConfiguration config, TimeProvider clock)
{
    public const int MaxFragments = 20;
    public const string PromptVersion = "profile-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json)!;
    private static string Id(string prefix, string text) => prefix + OfficeExtractor.Hash(Encoding.UTF8.GetBytes(text));
    public async Task<PipelinePlan> BuildAsync(JsonObject job, CancellationToken ct)
    {
        var tenant = job.Text("tenantId"); var partition = RecordPartition.Catalog(tenant);
        Dictionary<string, string?> expected = []; Dictionary<string, RecordWrite> writes = [];
        async Task<StoredRecord?> Read(string id)
        {
            var row = await repository.ReadAsync(partition, id, ct); if (expected.TryGetValue(id, out var prior) && prior != row?.ETag) throw new DomainException(409, "SOURCE_REVOKED");
            expected[id] = row?.ETag; return row;
        }
        void Write(JsonObject value) => writes[value.Text("id")] = new(value, expected[value.Text("id")]);
        var aggregate = await Read(job.Text("aggregateId")) ?? throw new DomainException(409, "SOURCE_REVOKED");
        if (aggregate.Value.Number("sourceVersion") != job.Number("aggregateVersion")) throw new DomainException(409, "SOURCE_REVOKED");
        var enrichment = job.Text("jobType") == "enrichment";
        var document = enrichment ? aggregate : null;
        var personId = enrichment ? aggregate.Value.Text("personId") : aggregate.Value.Text("id");
        var actor = config["Data:Mode"] == "Demo" && tenant == "demo" ? IdentityResolver.DemoPerson(personId) : identities.ResolvePerson(tenant, personId);
        if (actor is null || actor.PrincipalId != aggregate.Value.Text("ownerId")) throw new DomainException(409, "SOURCE_REVOKED");
        var person = enrichment ? await Read(personId) ?? throw new DomainException(409, "SOURCE_REVOKED") : aggregate;
        AccessPolicy.RequireSelf(actor, person.Value);
        if (person.Value.Text("publicationState") == "withdrawn") throw new DomainException(409, "SOURCE_REVOKED");
        var department = enrichment && document!.Value.Text("documentType") == "report";
        StoredRecord? departmentRecord = null;
        JsonObject entity;
        if (department)
        {
            var id = Id("activity-", actor.PrincipalId + ":" + document!.Value.Text("title"));
            departmentRecord = await Read(id);
            entity = departmentRecord?.Value.Copy() ?? RecordJson.New("departmentActivity", id, tenant, actor.PrincipalId, document.Value.Strings("scopeIds"), clock.GetUtcNow());
            entity["departmentId"] = person.Value.Text("departmentId"); entity["title"] = document.Value.Text("title"); entity["accessVersion"] = entity.Number("accessVersion") + 1;
        }
        else entity = person.Value.Copy();
        var fragments = new List<JsonObject>();
        if (!department)
            foreach (var id in person.Value.Strings("experienceIds"))
            {
                var row = await Read(id) ?? throw new DomainException(409, "SOURCE_REVOKED");
                if (row.Value.Text("personId") != personId) throw new DomainException(409, "SOURCE_REVOKED");
                fragments.Add(row.Value.Copy());
            }
        if (enrichment)
        {
            if (!AccessPolicy.CanUse(actor, document!.Value) || document.Value.Text("status") != "ready" ||
                document.Value.Text("enrichmentJobId") is { Length: > 0 } currentJob && currentJob != job.Text("id"))
                throw new DomainException(409, "SOURCE_REVOKED");
            if (document.Value.Text("importKey") is { Length: > 0 } importKey && person.Value["documentHeads"]?[importKey]?.GetValue<string>() != document.Value.Text("id"))
                throw new DomainException(409, "SOURCE_REVOKED");
            var touched = new HashSet<string>();
            var version = await Read(document.Value.Text("currentVersionId")) ?? throw new DomainException(409, "SOURCE_REVOKED");
            var spans = new List<StoredRecord>(); string? cursor = null;
            do
            {
                var page = await repository.QueryAsync(partition, "evidenceSpan", "documentVersionId", version.Value.Text("id"), 100, cursor, ct);
                spans.AddRange(page.Items); cursor = page.ContinuationToken;
                if (spans.Count > 100) throw new DomainException(422, "PROFILE_FRAGMENT_LIMIT");
            } while (cursor is not null);
            if (spans.Count == 0) throw new DomainException(422, "UNGROUNDED_EXTRACTION");
            var groups = spans.OrderBy(x => x.Value.Text("id"), StringComparer.Ordinal).GroupBy(RowKey).ToArray();
            if (groups.Length > MaxFragments) throw new DomainException(422, "PROFILE_FRAGMENT_LIMIT");
            foreach (var group in groups)
            {
                foreach (var row in group)
                {
                    expected[row.Value.Text("id")] = row.ETag;
                    if (row.Value.Text("validationState") != "validated" || row.Value.Number("sourceVersion") != document.Value.Number("activeVersion")) throw new DomainException(422, "UNGROUNDED_EXTRACTION");
                }
                var span = group.First();
                var text = string.Join(" ", group.Select(row => row.Value.Text("text")));
                var fragmentRefs = group.Select(row => new EvidenceReference(row.Value.Text("id"), version.Value.Text("id"), row.Value.Number("sourceVersion"))).ToArray();
                var reference = fragmentRefs[0];
                // Dates and cells stay mechanical. Only prose without a recognizable date range needs structuring.
                var period = ProfileExtraction.Period(text);
                string? structureRun = null;
                if (group.Count() == 1 && span.Value["location"]?["type"]?.GetValue<string>() == "paragraph" && period.Precision == "unknown")
                {
                    var structured = await structurer.StructureAsync(new(actor, job.Text("id"), PromptVersion, Retriever.TaxonomyVersion, job.Number("aggregateVersion"), [reference], []), text, ct);
                    structureRun = structured.RunId;
                    if (structured.Status != AiStatus.Succeeded || structured.Value is null) throw Failure(structured.Status, structured.Failure);
                    // Evidence IDs alone are insufficient. Require extractive claims to match the cited text.
                    if (structured.Value.Items.Length == 0 || structured.Value.Items.Any(i => string.IsNullOrWhiteSpace(i.Text) || !text.Contains(i.Text, StringComparison.Ordinal) ||
                        i.EvidenceIds.Length != 1 || i.EvidenceIds[0] != reference.Id)) throw new DomainException(422, "UNGROUNDED_EXTRACTION");
                }
                var fragmentId = Id("experience-", actor.PrincipalId + ":" + document.Value.Text("documentType") + ":" + document.Value.Text("title") + ":" + group.Key);
                touched.Add(fragmentId);
                var existing = department ? null : fragments.Find(x => x.Text("id") == fragmentId);
                if (!expected.ContainsKey(fragmentId)) await Read(fragmentId);
                var value = existing?.Copy() ?? RecordJson.New("experience", fragmentId, tenant, actor.PrincipalId, document.Value.Strings("scopeIds"), clock.GetUtcNow());
                if (existing?.Text("confirmationState") is "self_corrected" or "self_confirmed")
                {
                    if (existing.Text("description") != text || !JsonNode.DeepEquals(existing["period"], Node(period)))
                    {
                        value["correctionState"] = "conflict";
                        value["pendingAiVersion"] = document.Value.Number("sourceVersion");
                        value["pendingExtraction"] = new JsonObject { ["description"] = text, ["period"] = Node(period), ["evidenceRefs"] = Node(fragmentRefs) };
                    }
                }
                else
                {
                    value["description"] = text; value["period"] = Node(period); value["evidenceRefs"] = Node(fragmentRefs);
                    value["confirmationState"] = "ai_unconfirmed"; value["correctionState"] = "none"; value["pendingAiVersion"] = null;
                }
                value["importKey"] = document.Value.Text("importKey");
                value["title"] = document.Value.Text("title");
                value["personId"] = personId; value["documentId"] = document.Value.Text("id"); value["structureRunId"] = structureRun;
                value["processingMode"] = config["Ai:Mode"] == "Mock" ? "fixture" : "live";
                value["updatedAt"] = clock.GetUtcNow();
                value["sourceVersion"] = (existing?.Number("sourceVersion") ?? 0) + 1;
                fragments.RemoveAll(x => x.Text("id") == fragmentId); fragments.Add(value);
            }
            foreach (var removed in fragments.Where(f => f.Text("importKey") == document.Value.Text("importKey") && !touched.Contains(f.Text("id"))).ToArray())
            {
                if (removed.Text("confirmationState") is "self_corrected" or "self_confirmed")
                {
                    removed["correctionState"] = "conflict";
                    removed["pendingExtraction"] = new JsonObject { ["description"] = "再取込原本から削除されています", ["removed"] = true };
                }
                else fragments.Remove(removed);
            }
        }
        if (fragments.Count is 0 or > MaxFragments) throw new DomainException(422, "PROFILE_FRAGMENT_LIMIT");
        // Recheck every authoritative evidence dependency at commit, including old self-corrected fragments.
        foreach (var fragment in fragments)
            foreach (var reference in fragment["evidenceRefs"]!.Deserialize<EvidenceReference[]>(Json)!)
            {
                var span = await Read(reference.Id) ?? throw new DomainException(409, "SOURCE_REVOKED");
                var version = await Read(reference.DocumentVersionId) ?? throw new DomainException(409, "SOURCE_REVOKED");
                var doc = await Read(version.Value.Text("documentId")) ?? throw new DomainException(409, "SOURCE_REVOKED");
                if (span.Value.Text("documentVersionId") != reference.DocumentVersionId || span.Value.Text("validationState") != "validated" ||
                    span.Value.Number("sourceVersion") != reference.SourceVersion || version.Value.Text("status") != "ready" ||
                    doc.Value.Number("activeVersion") != reference.SourceVersion || !AccessPolicy.CanUse(actor, doc.Value)) throw new DomainException(409, "SOURCE_REVOKED");
            }
        var refs = fragments.SelectMany(f => f["evidenceRefs"]!.Deserialize<EvidenceReference[]>(Json)!).Distinct().ToArray();
        var combined = string.Join("\n", fragments.Select(f => f.Text("description")));
        if (combined.Length > 8000) throw new DomainException(422, "PROFILE_TEXT_LIMIT");
        entity["description"] = combined;
        entity["evidenceRefs"] = Node(refs);
        entity["sourceVersion"] = entity.Number("sourceVersion") + 1;
        entity["accessVersion"] = Math.Max(1, entity.Number("accessVersion"));
        entity["activeVersion"] = entity.Number("sourceVersion");
        entity["processingMode"] = config["Ai:Mode"] == "Mock" ? "fixture" : "live";
        if (entity.Text("confirmationState") is not ("self_corrected" or "self_confirmed")) entity["confirmationState"] = "ai_unconfirmed";
        entity["totalExperienceMonths"] = ProfileExtraction.TotalMonths(fragments.Select(f => f["period"]!.Deserialize<ExperiencePeriod>(Json)!));
        if (!department) entity["experienceIds"] = RecordJson.Array(fragments.Select(f => f.Text("id")));
        else { entity["summary"] = combined; entity["period"] = Node(new ExperiencePeriod(null, null, "unknown", null)); }
        entity["correctionState"] = fragments.Any(f => f.Text("correctionState") == "conflict") ? "conflict" : "none";
        async Task<SourceRef[]> SourceRefs(EvidenceReference[] references)
        {
            var result = new List<SourceRef>();
            foreach (var reference in references)
            {
                var span = (await Read(reference.Id))!.Value;
                var doc = (await Read(span.Text("documentId")))!.Value;
                result.Add(new(reference.Id, doc.Text("title"), span["location"]!["label"]!.GetValue<string>(), span.Text("text")));
            }
            return result.ToArray();
        }
        var projectionIds = new List<string>();
        var projectionFragments = new List<JsonObject>(fragments);
        var searchOnlyIds = new HashSet<string>();
        if (!department && config.GetValue<bool>("Profile:SecondaryWorkFragments"))
            foreach (var fragment in fragments)
            {
                var secondary = ProfileExtraction.SecondaryWorkText(fragment.Text("description"));
                if (secondary is null) continue;
                var references = fragment["evidenceRefs"]!.Deserialize<EvidenceReference[]>(Json)!;
                var sourceRefs = await SourceRefs(references);
                var grounded = references.Where(r => sourceRefs.Any(s => s.Id == r.Id && s.Text.Contains(secondary, StringComparison.Ordinal))).ToArray();
                // 本人訂正にしかない文言を原本引用として追加しない。元断片はそのまま再分類する。
                if (grounded.Length == 0) continue;
                var derived = fragment.Copy();
                derived["id"] = Id("secondary-", fragment.Text("id") + ":" + ProfileExtraction.SecondaryWorkRuleVersion);
                derived["description"] = secondary; derived["evidenceRefs"] = Node(grounded);
                projectionFragments.Add(derived); searchOnlyIds.Add(derived.Text("id"));
            }
        // 正本の月数・訂正対象は増やさず、検索断片だけを上限内で追加する。
        if (projectionFragments.Count > MaxFragments) throw new DomainException(422, "PROFILE_FRAGMENT_LIMIT");
        foreach (var fragment in projectionFragments.Append(entity))
        {
            var evidence = fragment["evidenceRefs"]!.Deserialize<EvidenceReference[]>(Json)!;
            var fingerprint = Id("classification-", RecordJson.Canonical(new JsonObject { ["text"] = fragment.Text("description"), ["refs"] = Node(evidence),
                ["prompt"] = PromptVersion, ["taxonomy"] = Retriever.TaxonomyVersion, ["mode"] = config["Ai:Mode"], ["model"] = config["Jev:Model"] ?? "mock-v1", ["entity"] = fragment.Text("id") }));
            var saved = await Read(fingerprint);
            Decisions answer;
            if (saved is not null) answer = saved.Value["decisions"]!.Deserialize<Decisions>(Json)!;
            else
            {
                var result = await decisions.DecideAsync(new(actor, job.Text("id"), PromptVersion, Retriever.TaxonomyVersion, entity.Number("sourceVersion"), evidence, []), fragment.Text("description"),
                    [new("domain", "state.textの経験分野を分類。根拠は訂正前の場合もある。Probabilityは年数・熟練度ではない。", ProfileExtraction.Domains)],
                    ProfileExtraction.Domains.Concat(new[] { new KeyValuePair<string, string>("handover", "引き継ぎ") }).Select(p => new NoulQuestion(p.Key, p.Value + "の経験がstate.textに明記されているか。根拠は本人訂正前の場合もある。", ProfileExtraction.HasDomainEvidence(fragment.Text("description"), p.Key))).ToArray(), ct);
                if (result.Status != AiStatus.Succeeded || result.Value is null) throw Failure(result.Status, result.Failure);
                answer = result.Value;
                var record = RecordJson.New("classificationResult", fingerprint, tenant, actor.PrincipalId, entity.Strings("scopeIds"), clock.GetUtcNow());
                var choice = answer.Choices["domain"];
                record["axis"] = "domain"; record["choice"] = choice.Choice; record["probabilities"] = Node(choice.Probabilities); record["confidence"] = choice.Confidence;
                record["origin"] = config["Ai:Mode"] == "Mock" ? "mock" : "ai";
                record["entityId"] = entity.Text("id"); record["unitId"] = fragment.Text("id"); record["decisions"] = Node(answer);
                record["dataVersion"] = entity.Number("sourceVersion");
                record["aiRunId"] = result.RunId; record["promptVersion"] = PromptVersion; record["taxonomyVersion"] = Retriever.TaxonomyVersion;
                record["evidenceRefs"] = Node(evidence); record["processingMode"] = entity.Text("processingMode");
                Write(record);
            }
            fragment["classificationResultId"] = fingerprint;
            fragment["decisions"] = Node(answer);
            var unitId = Id("projection-", entity.Text("id") + ":" + entity.Number("sourceVersion") + ":" + fragment.Text("id"));
            await Read(unitId);
            var unit = new SearchUnit(unitId, entity.Text("id"), entity.Text("kind"), tenant, fragment.Text("id"),
                department ? entity.Text("title") : entity.Text("displayName"), fragment.Text("description"), entity.Strings("scopeIds"), "active",
                department || entity["available"]!.GetValue<bool>(), Retriever.TaxonomyVersion,
                answer.Choices.SelectMany(q => q.Value.Probabilities.Select(p => new CategoryProbability(q.Key, p.Key, p.Value))).ToArray(),
                answer.Nouls.Select(p => new ExperienceSignal(p.Key, p.Value.YesProbability, p.Value.State switch { NoulState.Supported => "supported", NoulState.Unknown => "unknown", _ => "not_evaluated" })).ToArray(),
                ProfileExtraction.Terms(fragment.Text("description")), await SourceRefs(evidence), fragment.Text("confirmationState"))
            { SourceVersion = entity.Number("sourceVersion"), ProjectionVersion = entity.Number("sourceVersion"), AccessVersion = entity.Number("accessVersion"), ProjectionState = "ready", EvidenceRefs = evidence,
                SkillIds = ProfileExtraction.Skills(fragment.Text("description")), ProcessingMode = entity.Text("processingMode") };
            var projection = Node(unit).AsObject();
            foreach (var pair in RecordJson.New("searchUnit", unitId, tenant, actor.PrincipalId, entity.Strings("scopeIds"), clock.GetUtcNow()))
                if (!projection.ContainsKey(pair.Key)) projection[pair.Key] = pair.Value?.DeepClone();
            projection["classificationResultIds"] = RecordJson.Array([fingerprint]); Write(projection); projectionIds.Add(unitId);
            if (!department && fragment != entity && !searchOnlyIds.Contains(fragment.Text("id"))) Write(fragment);
        }
        if (department)
        {
            entity["fragments"] = Node(fragments.Select(f => { var value = f.Copy(); value["kind"] = "departmentActivityFragment"; value.Remove("personId"); return value; }).ToArray());
        }
        entity["updatedAt"] = clock.GetUtcNow();
        entity["projectionIds"] = RecordJson.Array(projectionIds); Write(entity);
        if (document is not null)
        {
            document.Value["aiState"] = "complete"; document.Value["processingMode"] = entity.Text("processingMode"); document.Value["profileResultId"] = entity.Text("id"); Write(document.Value);
        }
        return new(tenant, expected, writes.Values.ToArray(), entity.Text("id"));
    }
    private static string RowKey(StoredRecord span)
    {
        var label = span.Value["location"]!["label"]!.GetValue<string>();
        if (span.Value["location"]!["type"]!.GetValue<string>() == "cells")
        {
            var match = Regex.Match(label, @"^(.*!)[A-Z]+(\d+)(?::[A-Z]+\d+)?$");
            if (match.Success) return match.Groups[1].Value + "row-" + match.Groups[2].Value;
        }
        var table = Regex.Match(label, @"^(.*?) / paragraph \d+ / table (\d+) row (\d+) cell \d+$");
        return table.Success ? $"{table.Groups[1].Value} / table {table.Groups[2].Value} row {table.Groups[3].Value}" : label;
    }
    private static DomainException Failure(AiStatus status, AiFailure? failure) => new(422, status == AiStatus.BudgetStopped ? "AI_BUDGET_STOPPED" : "AI_" + (failure?.ToString().ToUpperInvariant() ?? "FAILED"));
    public Task CommitAsync(PipelinePlan plan, RecordWrite[] extra, CancellationToken ct) => fence.RunAsync(plan.Tenant, async token =>
    {
        var partition = RecordPartition.Catalog(plan.Tenant);
        foreach (var (id, etag) in plan.Expected)
            if ((await repository.ReadAsync(partition, id, token))?.ETag != etag) throw new DomainException(409, "SOURCE_REVOKED");
        await store.CommitAsync(partition, [.. plan.Writes, .. extra], token);
        return true;
    }, ct);
}
