using System.Text.Json;
using Dse.Domain;
using Dse.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Dse.Tests;

public sealed class WorkflowIngestionTests
{
    [Fact]
    public async Task Ordinary_upload_queue_worker_and_profile_pipeline_stay_in_the_durable_workflow()
    {
        var f=await SkillDictionaryFixture.Create();var queue=new Queue();var blobs=new Blobs();using var root=DemoWorkflowTests.Root(f.Store,queue,blobs);
        var manager=root.GetRequiredService<DemoWorkflows>();var context=await manager.StartAsync(SkillDictionaryFixture.Curator,default);
        var bytes=OfficeFixtures.Package(body:"<w:p><w:r><w:t>アプリ開発担当 ／ TypeScript、SQLite、Git</w:t></w:r></w:p>");
        var accepted=await context.Services.GetRequiredService<DocumentUploads>().UploadAsync(context.Actor,"架空技術資料","report","fictional.docx",OfficeExtractor.WordMime,bytes,Guid.NewGuid().ToString("N"),default);
        var id=accepted.Body!["documentId"]!.GetValue<string>();Assert.Equal(context.Id,Assert.Single(queue.Notices).WorkflowId);
        var processor=root.GetRequiredService<IngestionProcessor>();for(var i=0;i<8&&queue.Pending>0;i++)await processor.RunOnceAsync(default);
        var store=context.Services.GetRequiredService<IRecordStore>();var document=(await store.ReadAsync(RecordPartition.Catalog("demo"),id,default))!.Value;
        var jobs=await store.QueryAsync(RecordPartition.Catalog("demo"),"job",null,null,100,null,default);
        Assert.True(document.Text("aiState")=="complete",string.Join('\n',jobs.Items.Select(j=>j.Value.ToJsonString())));Assert.Equal("fixture",document.Text("processingMode"));Assert.NotEqual("",document.Text("profileResultId"));
        Assert.Null(await f.Repo.ReadAsync(RecordPartition.Catalog("demo"),id,default));
        Assert.All(queue.Notices,n=>Assert.Equal(context.Id,n.WorkflowId));Assert.True(queue.Deleted>=2);
    }

    [Fact]
    public async Task Blob_keys_and_conversation_queue_notices_keep_the_same_isolation_boundary()
    {
        var f=await SkillDictionaryFixture.Create();var queue=new Queue();var blobs=new Blobs();using var root=DemoWorkflowTests.Root(f.Store,queue,blobs);
        var context=await root.GetRequiredService<DemoWorkflows>().StartAsync(SkillDictionaryFixture.Curator,default);
        var scoped=context.Services.GetRequiredService<IIngestionBlobs>();await scoped.PutAsync("originals","logical-key",[1,2,3],default);
        Assert.False(blobs.Values.ContainsKey(("originals","logical-key")));Assert.Equal(new byte[]{1,2,3},await scoped.GetAsync("originals","logical-key",default));
        await context.Services.GetRequiredService<IIngestionQueue>().SendAsync(new("demo","job-fictional",1,"session-fictional"),default);
        var notice=Assert.Single(queue.Notices);Assert.Equal(context.Id,notice.WorkflowId);Assert.Equal("session-fictional",notice.SessionId);
    }

    internal sealed class Queue:IIngestionQueue
    {
        private readonly System.Collections.Generic.Queue<QueueDelivery> pending=new();
        public List<QueueNotice> Notices=[];public int Deleted;public int Pending=>pending.Count;
        public Task SendAsync(QueueNotice notice,CancellationToken ct,bool poison=false){Notices.Add(notice);if(!poison)pending.Enqueue(new(Guid.NewGuid().ToString("N"),"receipt",JsonSerializer.Serialize(notice)));return Task.CompletedTask;}
        public Task<QueueDelivery?> ReceiveAsync(TimeSpan visibility,CancellationToken ct)=>Task.FromResult(pending.TryDequeue(out var delivery)?delivery:null);
        public Task<QueueDelivery> RenewAsync(QueueDelivery delivery,TimeSpan visibility,CancellationToken ct)=>Task.FromResult(delivery);
        public Task DeleteAsync(QueueDelivery delivery,CancellationToken ct){Deleted++;return Task.CompletedTask;}
    }
    internal sealed class Blobs:IIngestionBlobs
    {
        public Dictionary<(string,string),byte[]> Values=[];
        public Task PutAsync(string container,string key,byte[] bytes,CancellationToken ct){Values[(container,key)]=bytes;return Task.CompletedTask;}
        public Task<byte[]> GetAsync(string container,string key,CancellationToken ct)=>Task.FromResult(Values[(container,key)]);
        public async IAsyncEnumerable<BlobEntry> ListAsync(string container,string prefix,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct){await Task.CompletedTask;foreach(var key in Values.Keys.Where(k=>k.Item1==container&&k.Item2.StartsWith(prefix,StringComparison.Ordinal)))yield return new(key.Item2,DateTimeOffset.UtcNow,"etag");}
        public Task DeleteAsync(string container,string key,string etag,CancellationToken ct){Values.Remove((container,key));return Task.CompletedTask;}
    }
}
