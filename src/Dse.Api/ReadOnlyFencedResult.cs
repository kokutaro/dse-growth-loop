using Dse.Domain;
using Dse.Infrastructure;

/// <summary>読取り専用の結果とHTTP writerを同じfence内で完了させ、取消時の終了証明を提供します。</summary>
public sealed class ReadOnlyFencedResult(AccessFence fence,string tenant,Func<IPrimaryRepository,CancellationToken,Task<IResult>> build,CancellationToken ct):IResult
{
    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext context)=>fence.RunReadOnlyAsync(tenant,async(reads,token)=>
    {
        if(context.Items[typeof(ActorAccess)]is ActorAccess actor)await new AuthorityProvider(reads).RequireAsync(actor,token);
        var original=context.Response.Body;
        using var tracked=new ReadOnlyResponseStream(original);
        try
        {
            context.Response.Body=tracked;
            await(await build(reads,token)).ExecuteAsync(context);
            return true;
        }
        finally{context.Response.Body=original;}
    },ct);

    // No ownership of the server stream. Pending asynchronous operations retain
    // their tracker even after this wrapper is detached from HttpResponse.
    private sealed class ReadOnlyResponseStream(Stream inner):Stream
    {
        public override bool CanRead=>inner.CanRead;
        public override bool CanSeek=>inner.CanSeek;
        public override bool CanWrite=>inner.CanWrite;
        public override long Length=>inner.Length;
        public override long Position{get=>inner.Position;set=>inner.Position=value;}
        public override void Flush(){using var work=AccessFence.TrackReadOnlyResponseWork();inner.Flush();}
        public override async Task FlushAsync(CancellationToken token){using var work=AccessFence.TrackReadOnlyResponseWork();await inner.FlushAsync(token);}
        public override void Write(byte[] buffer,int offset,int count){using var work=AccessFence.TrackReadOnlyResponseWork();inner.Write(buffer,offset,count);}
        public override void Write(ReadOnlySpan<byte> buffer){using var work=AccessFence.TrackReadOnlyResponseWork();inner.Write(buffer);}
        public override async Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken token){using var work=AccessFence.TrackReadOnlyResponseWork();await inner.WriteAsync(buffer,offset,count,token);}
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default){using var work=AccessFence.TrackReadOnlyResponseWork();await inner.WriteAsync(buffer,token);}
        public override int Read(byte[] buffer,int offset,int count)=>inner.Read(buffer,offset,count);
        public override long Seek(long offset,SeekOrigin origin)=>inner.Seek(offset,origin);
        public override void SetLength(long value){using var work=AccessFence.TrackReadOnlyResponseWork();inner.SetLength(value);}
    }
}
