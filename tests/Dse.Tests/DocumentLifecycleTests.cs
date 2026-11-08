using System.Text.Json.Nodes;
using Dse.Domain;
using Dse.Infrastructure;
using Xunit;

namespace Dse.Tests;

public sealed class DocumentLifecycleTests
{
    [Fact]
    public async Task Correction_revokes_old_quotes_uses_the_same_worker_and_is_idempotent()
    {
        using var h=new IngestionHarness();var accepted=await h.Upload();await h.Processor.RunOnceAsync(default);
        var p=RecordPartition.Catalog("demo");var id=accepted.Body!["documentId"]!.GetValue<string>();var original=(await h.Repo.ReadAsync(p,id,default))!;
        var bytes=OfficeFixtures.Package(body:"<w:p><w:r><w:t>訂正した架空のGit資料。</w:t></w:r></w:p>");
        var next=await h.Uploads.ReviseAsync(IdentityResolver.Demo,id,original.ETag,"document-correction-1","架空資料の誤記を訂正","revision.docx",OfficeExtractor.WordMime,bytes,default);
        Assert.Equal(2,next.Body!["version"]!.GetValue<int>());var pending=(await h.Repo.ReadAsync(p,id,default))!;
        Assert.Null(pending.Value["activeVersion"]);Assert.Equal(2,pending.Value.Number("accessVersion"));
        Assert.Null(await new SourceGuard(h.Repo).EvidenceAsync(IdentityResolver.Demo,new(id+"-v1-e00000000",id+"-v1",1),default));
        var replay=await h.Uploads.ReviseAsync(IdentityResolver.Demo,id,original.ETag,"document-correction-1","架空資料の誤記を訂正","revision.docx",OfficeExtractor.WordMime,bytes,default);
        Assert.True(JsonNode.DeepEquals(next.Body,replay.Body));
        await h.Processor.RunOnceAsync(default);var done=(await h.Repo.ReadAsync(p,id,default))!;Assert.Equal(2,done.Value.Number("activeVersion"));
        var evidence=(await h.Repo.QueryAsync(p,"evidenceSpan","documentVersionId",id+"-v2",100,null,default)).Items;
        Assert.Equal("訂正した架空のGit資料。",Assert.Single(evidence).Value.Text("text"));
        Assert.NotNull(await h.Repo.ReadAsync(p,id+"-v1",default));
    }

    [Fact]
    public async Task Withdrawal_is_audited_blocks_pending_jobs_and_requires_current_owner_and_etag()
    {
        using var h=new IngestionHarness();var accepted=await h.Upload();var p=RecordPartition.Catalog("demo");var id=accepted.Body!["documentId"]!.GetValue<string>();var doc=(await h.Repo.ReadAsync(p,id,default))!;
        Assert.Equal(404,(await Assert.ThrowsAsync<DomainException>(()=>h.Uploads.WithdrawAsync(IdentityResolver.DemoPerson("demo-person-02")!,id,doc.ETag,"withdraw-other-owner","本人の原文を撤回",default))).Status);
        Assert.Equal(412,(await Assert.ThrowsAsync<DomainException>(()=>h.Uploads.WithdrawAsync(IdentityResolver.Demo,id,"\"stale\"","withdraw-stale-owner","本人の原文を撤回",default))).Status);
        var result=await h.Uploads.WithdrawAsync(IdentityResolver.Demo,id,doc.ETag,"withdraw-current-owner","本人の原文を撤回",default);Assert.Equal(200,result.Status);
        await h.Processor.RunOnceAsync(default);doc=(await h.Repo.ReadAsync(p,id,default))!;
        Assert.Equal("withdrawn",doc.Value.Text("status"));Assert.Null(doc.Value["activeVersion"]);
        var audit=(await h.Repo.ReadAsync(p,result.Body!["auditId"]!.GetValue<string>(),default))!;
        Assert.Equal("document_withdrawn",audit.Value.Text("action"));Assert.Equal(id,audit.Value.Text("aggregateId"));
        Assert.Equal("cancelled",(await h.Job(accepted)).Value.Text("status"));
    }
}
