using System.Text.Json;
using System.Text.Json.Nodes;
using Dse.Domain;

namespace Dse.Infrastructure;

public sealed record SkillDemoSession(string Id,DateTimeOffset ExpiresAt,ActorAccess Actor,IPrimaryRepository Repository,AccessFence Fence,SkillDictionaryService Dictionary,string Binding);

// All business records live in memory. Only the requesting operator's current authority
// is read from the shared store; neither real sources nor shared dictionaries are copied.
public sealed class SkillDemoSessions(IPrimaryRepository authority,TimeProvider clock)
{
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly Dictionary<string,SkillDemoSession> sessions=[];
    public static bool EditableSource(string id)=>id is "fragment-technical-list-a-distinct" or "fragment-technical-list-b-distinct";
    private static string Binding(ActorAccess a)=>a.TenantId+":"+a.PrincipalId+":"+a.PersonId+":"+a.AccessEpoch+":"+string.Join(",",a.ScopeIds.Order())+":"+string.Join(",",a.Roles.Order());
    private async Task<ActorAccess> Require(ActorAccess actor,CancellationToken ct)
    {
        var current=await new AuthorityProvider(authority).RequireAsync(actor,ct);
        if(!current.HasRole("operator")||!current.HasRole("member"))throw new DomainException(403,"FORBIDDEN");
        return current;
    }
    public async Task<SkillDemoSession> StartAsync(ActorAccess actor,CancellationToken ct)
    {
        actor=await Require(actor,ct);await gate.WaitAsync(ct);
        try
        {
            foreach(var id in sessions.Where(x=>x.Value.ExpiresAt<=clock.GetUtcNow()).Select(x=>x.Key).ToArray())sessions.Remove(id);
            var binding=Binding(actor);var existing=sessions.Values.SingleOrDefault(s=>s.Binding==binding);
            if(existing is not null)return existing;
            if(sessions.Count>=8)throw new DomainException(409,"SKILL_DEMO_CAPACITY");
            var store=new MemoryRecordStore();var repo=new DemoRepository(store,authority,actor);var fence=new AccessFence(repo);
            var dictionary=new SkillDictionaryService(repo,fence,new(repo,clock),clock); // No decision factory: external AI is impossible.
            foreach(var (name,text,hints) in new[]{
                ("technical-list-a","アプリ開発担当 ／ TypeScript、SQLite、Git",new[]{"TypeScript","SQLite","Git"}),
                ("technical-list-b","通信処理の開発担当 ／ Go、Linux、Git",new[]{"Go","Linux","Git"})})
            foreach(var distinct in new[]{false,true})
            {
                var id=name+(distinct?"-distinct":"");var now=clock.GetUtcNow();
                JsonObject Row(string kind,string key)=>RecordJson.New(kind,key,actor.TenantId,actor.PrincipalId,actor.ScopeIds,now);
                var doc=Row("document","doc-"+id);doc["activeVersion"]=1;doc["accessVersion"]=1;doc["status"]="ready";
                var ver=Row("documentVersion","ver-"+id);ver["documentId"]=doc.Text("id");ver["status"]="ready";
                var evidence=Row("evidenceSpan","ev-"+id);evidence["documentId"]=doc.Text("id");evidence["documentVersionId"]=ver.Text("id");evidence["validationState"]="validated";evidence["text"]=text;
                var fragment=Row("experience","fragment-"+id);fragment["personId"]=actor.PersonId;fragment["description"]=text;
                fragment["evidenceRefs"]=JsonSerializer.SerializeToNode(new[]{new EvidenceReference(evidence.Text("id"),ver.Text("id"),1)},ProjectionRules.Json);
                fragment["specificSkills"]=JsonSerializer.SerializeToNode(new SpecificSkills(SpecificSkillRules.Version,"extracted",hints.Select(h=>new SpecificSkill(distinct?h:text,h,evidence.Text("id"),text){Basis="knowledge"}).ToArray()),ProjectionRules.Json);
                await store.CommitAsync(RecordPartition.Catalog(actor.TenantId),[RecordWrite.Create(doc),RecordWrite.Create(ver),RecordWrite.Create(evidence),RecordWrite.Create(fragment)],ct);
                await dictionary.CollectAsync(actor,Guid.NewGuid().ToString("N"),new(fragment.Text("id")),ct);
            }
            var session=new SkillDemoSession(Guid.NewGuid().ToString("N"),clock.GetUtcNow().AddHours(1),actor,repo,fence,dictionary,binding);
            sessions.Add(session.Id,session);return session;
        }
        finally{gate.Release();}
    }
    public async Task<SkillDemoSession> GetAsync(ActorAccess actor,string id,CancellationToken ct)
    {
        actor=await Require(actor,ct);await gate.WaitAsync(ct);
        try
        {
            if(!Guid.TryParseExact(id,"N",out _)||!sessions.TryGetValue(id,out var session))throw new DomainException(410,"SKILL_DEMO_EXPIRED");
            if(session.Binding!=Binding(actor))throw new DomainException(404,"NOT_FOUND");
            if(session.ExpiresAt<=clock.GetUtcNow())throw new DomainException(410,"SKILL_DEMO_EXPIRED");
            return session;
        }
        finally{gate.Release();}
    }
    private sealed class DemoRepository(IRecordStore memory,IPrimaryRepository shared,ActorAccess actor):IPrimaryRepository
    {
        private static bool IsAuthority(string id)=>id==AuthorityProvider.ControlId||id.StartsWith("authority-",StringComparison.Ordinal);
        private void Partition(RecordPartition p){if(p!=RecordPartition.Catalog(actor.TenantId))throw new ArgumentException("Demo catalog only");}
        public Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)
        {
            Partition(p);
            if(id==AuthorityProvider.ControlId||id==AuthorityProvider.PersonId(actor.PersonId))return shared.ReadAsync(p,id,ct);
            return IsAuthority(id)?Task.FromResult<StoredRecord?>(null):memory.ReadAsync(p,id,ct);
        }
        public Task<RecordPage> QueryAsync(RecordPartition p,string kind,string? field,string? parent,int size,string? cursor,CancellationToken ct)
        {Partition(p);if(kind is "authorityControl" or "personAuthority")throw new ArgumentException("Authority queries are not part of the demo");return memory.QueryAsync(p,kind,field,parent,size,cursor,ct);}
        public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)
        {Partition(p);if(writes.Any(w=>IsAuthority(w.Value.Text("id"))))throw new ArgumentException("Demo cannot change authority");return memory.CommitAsync(p,writes,ct);}
    }
}
