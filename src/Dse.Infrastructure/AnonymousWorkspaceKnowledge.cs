using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed partial class AnonymousWorkspaceService
{
    private static string Token(JsonObject map, string collection, string value, string prefix, ref bool changed)
    {
        var tokens = map[collection]?.AsObject();
        if (tokens is null) { tokens = new JsonObject(); map[collection] = tokens; }
        foreach (var pair in tokens) if (pair.Value!.GetValue<string>() == value) return pair.Key;
        var token = Opaque(prefix); tokens[token] = value; changed = true; return token;
    }
    public async Task<AnonymousWorkspaceKnowledge> KnowledgeUnderFenceAsync(ActorAccess actor, AnonymousWorkspaceRoomResolution room, CancellationToken ct)
    {
        var state = await knowledge.StateAsync(actor, room.SessionId, ct); var map = await ReadMapping(actor, room.PublicRoomId, "room", ct);
        var changed = false;
        var metadata = BusinessAnalysisMetadataContract.Parse(state["analysisMetadata"]);
        if (metadata?.DepartmentId is string department) metadata = metadata with { DepartmentId = Token(map.Value,"departmentTokens",department,"awd-",ref changed) };
        var labels = new Dictionary<string,string>(); var evidenceLabels = new Dictionary<string,string>();
        var scopes = state.Strings("scopeIds").Where(actor.ScopeIds.Contains).Select((s,i) => { var token = Token(map.Value,"scopeTokens",s,"aws-",ref changed); labels[token] = "公開範囲" + (i+1); return token; }).ToArray();
        var evidence = state.Strings("evidenceIds").Select((s,i) => { var token = Token(map.Value,"evidenceTokens",s,"awe-",ref changed); evidenceLabels[token] = "根拠" + (i+1); return token; }).ToArray();
        var sections = new List<AnonymousWorkspaceSection>();
        foreach (var section in state["sections"]?.Deserialize<SummarySection[]>(Json) ?? [])
            sections.Add(new(section.Field,await DisplayTextAsync(actor,section.Text,room.Participants,ct),section.Basis,
                section.EvidenceIds.Select(id => Token(map.Value,"evidenceTokens",id,"awe-",ref changed)).ToArray()));
        var participantLabels = new Dictionary<string,string>(); var consents = new Dictionary<string,AnonymousWorkspaceConsent>();
        foreach (var alias in map.Value["aliases"]!.AsArray().Select(x => x!.AsObject()))
        {
            var aid = alias.Text("aliasId"); var self = alias.Text("internalPersonId") == actor.PersonId;
            participantLabels[aid] = self ? "あなた" : alias.Text("aliasLabel");
            if (state["consents"]?[alias.Text("internalPersonId")] is JsonObject c)
                consents[aid] = new(c.Number("version"),c["publication"]?.GetValue<bool>() == true,c["analysis"]?.GetValue<bool>() == true,self);
        }
        var feedback = (state["feedback"]?.AsArray() ?? []).Select(x => x!.AsObject()).Select(f =>
        {
            var alias = map.Value["aliases"]!.AsArray().Select(x => x!.AsObject()).SingleOrDefault(x => x.Text("internalPrincipalId") == f.Text("evaluatorId"));
            return alias is null ? null : new AnonymousWorkspaceFeedback(alias.Text("aliasId"),f["value"]?.GetValue<int>());
        }).OfType<AnonymousWorkspaceFeedback>().ToArray();
        if (changed) await repository.CommitAsync(Catalog(actor),[RecordWrite.Replace(map.Value,map.ETag)],ct);
        var outcome = state["outcome"] as JsonObject; var job = state["publicationJob"] as JsonObject;
        return new(state.Text("etag"),state.Text("sessionEtag"),state.Text("status"),state.Number("sourceVersion"),sections.ToArray(),scopes,evidence,
            labels,evidenceLabels,participantLabels,consents,state["canApprove"]?.GetValue<bool>() == true,state.Text("processingMode"),
            outcome is null ? null : new(outcome.Text("resolution"),await DisplayTextAsync(actor,outcome.Text("result"),room.Participants,ct)),feedback,
            job is null ? null : new(job.Text("status"),string.IsNullOrEmpty(job.Text("lastErrorCode")) ? null : job.Text("lastErrorCode")),
            metadata);
    }
    private async Task<JsonObject> TranslateKnowledgeAsync(ActorAccess actor, AnonymousWorkspaceRoomResolution room, string operation, JsonObject input, CancellationToken ct)
    {
        KnowledgeService.ValidateInput(operation,input); var translated = input.Copy();
        var texts = operation == "edit" ? input["sections"]!.AsArray().Select(x => x!.AsObject().Text("text"))
            : operation == "outcome" ? [input.Text("result")] : operation == "feedback" ? [input.Text("comment")] : Array.Empty<string>();
        await FreeInputAsync(actor,texts,room.Participants,ct);
        if (operation != "edit") return translated;
        var map = await ReadMapping(actor,room.PublicRoomId,"room",ct); var state = await knowledge.StateAsync(actor,room.SessionId,ct);
        string Resolve(string collection,string token,string[] allowed,string prefix)
        {
            PublicId(token,prefix); var value = map.Value[collection]?[token]?.GetValue<string>();
            if (value is null || !allowed.Contains(value)) throw new DomainException(403,"FORBIDDEN"); return value;
        }
        if (translated["analysisMetadata"] is JsonObject metadata && metadata["departmentId"] is JsonNode departmentNode)
        {
            var departmentToken = departmentNode.GetValue<string>(); PublicId(departmentToken,"awd-");
            var department = map.Value["departmentTokens"]?[departmentToken]?.GetValue<string>() ?? throw new DomainException(403,"FORBIDDEN");
            metadata["departmentId"] = department;
        }
        var session = await chat.SessionAsync(actor,room.SessionId,ct);
        translated["scopeIds"] = RecordJson.Array(input.Strings("scopeIds").Select(t => Resolve("scopeTokens",t,session.Value.Strings("scopeIds").Intersect(actor.ScopeIds).ToArray(),"aws-")));
        foreach (var node in translated["sections"]!.AsArray())
        {
            if (node is not JsonObject section || section.Any(p => p.Key is not ("field" or "text" or "basis" or "evidenceIds"))) throw new ArgumentException("Invalid section");
            section["evidenceIds"] = RecordJson.Array(section.Strings("evidenceIds").Select(t => Resolve("evidenceTokens",t,state.Strings("evidenceIds"),"awe-")));
        }
        return translated;
    }
    public async Task<string> ChangeKnowledgeAsync(ActorAccess actor,string roomId,string operation,string key,string etag,JsonObject input,CancellationToken ct)
    {
        // Existing KnowledgeService owns its own fence and rechecks membership/ETag before every commit.
        var prepared = await ConsumeRoomAsync(actor,roomId,async (room,token) =>
            (room.SessionId, Input: operation == "draft" ? input.Copy() : await TranslateKnowledgeAsync(actor,room,operation,input,token)),ct);
        if (operation == "draft")
        {
            if (input.Count != 0) throw new ArgumentException("Invalid draft input");
            await knowledge.GenerateAsync(actor,prepared.SessionId,key,etag,ct);
        }
        else
        {
            var state = await knowledge.ChangeAsync(actor,prepared.SessionId,operation,key,etag,prepared.Input,ct);
            if (operation == "approve")
            {
                try { await knowledge.ProcessPublicationAsync(actor.TenantId,prepared.SessionId,state.Number("sourceVersion"),ct); }
                catch (Exception e) { await knowledge.RecordPublicationFailureAsync(actor.TenantId,prepared.SessionId,state.Number("sourceVersion"),e,ct); }
            }
        }
        return roomId;
    }
    public async Task<int> RecoverKnowledgeAsync(ActorAccess actor,string roomId,CancellationToken ct)
    {
        var sid = await ConsumeRoomAsync(actor,roomId,(room,_) =>
        { if (!room.IsOwner && !actor.HasRole("publisher")) throw new DomainException(403,"FORBIDDEN"); return Task.FromResult(room.SessionId); },ct);
        return await knowledge.RecoverAsync(actor.TenantId,sid,ct);
    }
    public async Task<AnonymousWorkspacePage<AnonymousWorkspaceCase>> CasesUnderFenceAsync(ActorAccess actor,string? query,CancellationToken ct)
    {
        var request = new SearchRequest([],[],string.IsNullOrWhiteSpace(query) ? [] : ProfileExtraction.Terms(query));
        if (query is not null) SearchInput.RequireSafe(query);
        if (request.Terms.Length > 0) request.Validate();
        using var budget = new SearchBudget(); var raw = request.Terms.Length == 0
            ? await projections.BrowseKnowledgeAsync(actor.Search,ct) : await projections.FindAsync(request,actor.Search,ct);
        var units = await guard.FilterUnderFenceAsync(actor,raw.Where(u => u.EntityType == "knowledge"),ct);
        var terms = request.Terms.Select(Retriever.Normalize).ToHashSet(StringComparer.Ordinal);
        List<AnonymousWorkspaceCase> result = [];
        foreach (var unit in units.Where(u => terms.Count == 0 || u.NormalizedTerms.Any(terms.Contains)).DistinctBy(u => u.EntityId))
        {
            var row = await repository.ReadAsync(Catalog(actor),unit.EntityId,ct);
            // Fixture catalogue without a reviewed conversations authority is not a publishable case.
            if (row is null || row.Value.Text("contractVersion") != "knowledge-v1") continue;
            var idx = Index("case",unit.EntityId,unit.SourceVersion!.Value.ToString()); var existing = await repository.ReadAsync(Catalog(actor),idx,ct); string id;
            if (existing is null)
            {
                id = Opaque("awk-"); var pointer = Mapping(actor,idx,"case-index",""); pointer["publicCaseId"] = id;
                var map = Mapping(actor,id,"case",""); map["unit"] = JsonSerializer.SerializeToNode(unit,Json);
                await repository.CommitAsync(Catalog(actor),[RecordWrite.Create(pointer),RecordWrite.Create(map)],ct);
            }
            else id = existing.Value.Text("publicCaseId");
            result.Add(await CaseUnderFenceAsync(actor,id,ct));
            if (result.Count == 100) break;
        }
        return new(result.ToArray());
    }
    public async Task<AnonymousWorkspaceCase> CaseUnderFenceAsync(ActorAccess actor,string id,CancellationToken ct)
    {
        PublicId(id,"awk-"); var map = await ReadMapping(actor,id,"case",ct); var unit = map.Value["unit"]!.Deserialize<SearchUnit>(Json)!;
        await guard.RequireAsync(actor,unit,ct); var source = (await repository.ReadAsync(Catalog(actor),unit.EntityId,ct))!.Value;
        if (source.Text("contractVersion") != "knowledge-v1") throw new DomainException(404,"NOT_FOUND");
        var approved = await KnowledgeService.PublishedAsync(repository,actor.TenantId,source.Text("sessionId"),unit.SourceVersion!.Value,ct) ?? throw new DomainException(409,"SOURCE_REVOKED");
        var sections = approved["sections"]!.Deserialize<SummarySection[]>(Json)!.Select(s => new AnonymousWorkspacePublishedSection(s.Field,s.Text,s.Basis)).ToArray();
        var consenting = await KnowledgeService.CurrentConsentParticipantsAsync(repository,config,actor.TenantId,approved,false,ct)
            ?? throw new DomainException(409,"SOURCE_REVOKED");
        var participants = consenting.Select(p => new AnonymousWorkspaceResolvedParticipant(p.Identity.PersonId,p.Identity.PrincipalId,"","",false)).ToArray();
        await FreeInputAsync(actor,sections.Select(s => s.Text).Append(source.Text("title")),participants,ct);
        return new(id,source.Text("title"),string.Join("\n",sections.Select(s => s.Text)),sections,unit.SourceVersion.Value,source.Text("processingMode"));
    }
}
