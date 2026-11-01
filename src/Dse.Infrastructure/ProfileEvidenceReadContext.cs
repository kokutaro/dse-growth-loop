using System.Text;
using System.Text.Json.Nodes;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

// Created and consumed inside one read-only fence phase. Never an authorization ticket
// or an AI-wait cache. Only immutable source references / expected versions leave the phase.
internal sealed record ProfileEvidenceSnapshot(EvidenceReference Reference, SourceRef Source,
    string SpanEtag, string VersionEtag, string DocumentEtag, string TextSha256);
internal sealed class ProfileEvidenceReadContext(IPrimaryRepository reads,RecordPartition partition,
    Dictionary<string,string?> expected,IConfiguration configuration) : IPrimaryRepository
{
    private readonly Dictionary<string,StoredRecord?> observed=[];
    public async Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)
    {
        if(p!=partition)throw new DomainException(409,"SOURCE_REVOKED");
        var row=await reads.ReadAsync(p,id,ct);
        if(expected.TryGetValue(id,out var etag)&&etag!=row?.ETag)throw new DomainException(409,"SOURCE_REVOKED");
        expected[id]=row?.ETag;observed[id]=row;
        return row;
    }
    public Task<RecordPage> QueryAsync(RecordPartition p,string kind,string? field,string? parent,int size,string? token,CancellationToken ct)=>throw new InvalidOperationException("Point source reads only");
    public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct)
    {FencedReadPhase.RequireWritable();throw new InvalidOperationException("Read-only source context");}
    public async Task<ProfileEvidenceSnapshot[]> CaptureAsync(ActorAccess actor,IEnumerable<EvidenceReference> references,CancellationToken ct)
    {
        actor=await new AuthorityProvider(this,configuration).RequireAsync(actor,ct);
        var guard=new SourceGuard(this,configuration);List<ProfileEvidenceSnapshot> result=[];
        foreach(var reference in references)
        {
            ct.ThrowIfCancellationRequested();
            var source=await guard.EvidenceAsync(actor,reference,ct)??throw new DomainException(409,"SOURCE_REVOKED");
            if(source.Span.Text("kind")!="evidenceSpan")throw new DomainException(409,"SOURCE_REVOKED");
            if(source.Document.Text("generationParentId")!=""&&source.Document.Text("generationState")!="published")
                await GenerationAccess.RequireProcessingAsync(this,actor,source.Document,ct);
            var span=observed[reference.Id]??throw new DomainException(409,"SOURCE_REVOKED");
            var version=observed[reference.DocumentVersionId]??throw new DomainException(409,"SOURCE_REVOKED");
            var document=observed[source.Document.Text("id")]??throw new DomainException(409,"SOURCE_REVOKED");
            result.Add(new(reference,new(reference.Id,source.Document.Text("title"),source.Span["location"]!["label"]!.GetValue<string>(),source.Span.Text("text")),
                span.ETag,version.ETag,document.ETag,OfficeExtractor.Hash(Encoding.UTF8.GetBytes(source.Span.Text("text")))));
        }
        ct.ThrowIfCancellationRequested();
        return result.ToArray();
    }
}
