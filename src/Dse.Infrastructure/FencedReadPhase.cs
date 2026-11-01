using Dse.Domain;

namespace Dse.Infrastructure;

// One pre-send read set inside an already-owned fence. Never reused after AI or another phase.
internal sealed class FencedReadPhase(IPrimaryRepository inner) : IPrimaryRepository, IDisposable
{
    private static readonly AsyncLocal<FencedReadPhase?> Ambient = new();
    private readonly FencedReadPhase? previous = Ambient.Value;
    private readonly object sync = new();
    private readonly Dictionary<(RecordPartition,string),StoredRecord?> reads=[];
    private readonly PipelineDiagnostics.Stage stage=PipelineDiagnostics.Measure("source_preparation");
    private bool closed,violation;private int active;
    public static bool IsActive=>Ambient.Value is not null;
    public static FencedReadPhase Begin(IPrimaryRepository inner) { var phase=new FencedReadPhase(inner);Ambient.Value=phase;return phase; }
    public static void RequireWritable()
    {
        if(Ambient.Value is not { } phase)return;
        lock(phase.sync)phase.violation=true;
        throw new InvalidOperationException("Writes are forbidden during fenced source preparation");
    }
    private void Enter(){lock(sync){if(closed)throw new InvalidOperationException("Read phase closed");active++;}}
    private void Exit(){lock(sync)active--;}
    public async Task<StoredRecord?> ReadAsync(RecordPartition p,string id,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();Enter();
        try
        {
            StoredRecord? row;
            lock(sync)if(reads.TryGetValue((p,id),out row)){stage.CacheHit();return Copy(row);}
            row=await inner.ReadAsync(p,id,ct);
            lock(sync)reads[(p,id)]=Copy(row);
            return Copy(row);
        }
        finally{Exit();}
    }
    public async Task<RecordPage> QueryAsync(RecordPartition p,string kind,string? field,string? parent,int size,string? cursor,CancellationToken ct)
    {ct.ThrowIfCancellationRequested();Enter();try{return await inner.QueryAsync(p,kind,field,parent,size,cursor,ct);}finally{Exit();}}
    public Task CommitAsync(RecordPartition p,IReadOnlyList<RecordWrite> writes,CancellationToken ct){RequireWritable();throw new InvalidOperationException("Read-only phase");}
    private static StoredRecord? Copy(StoredRecord? row)=>row is null?null:new(row.Value.Copy(),row.ETag);
    public void Failed(Exception error)=>stage.Failed(error);
    public bool CloseForCancellation()
    {
        lock(sync){closed=true;return !violation && active==0;}
    }
    public void Dispose(){lock(sync)closed=true;stage.Dispose();Ambient.Value=previous;}
}
