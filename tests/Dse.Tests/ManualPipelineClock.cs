namespace Dse.Tests;

// Drives the real TimeProvider-based 120s / 60s deadlines without sleeping for minutes.
public sealed class ManualPipelineClock : TimeProvider
{
    private readonly object sync=new();private long ticks;
    private readonly List<Timer> timers=[];
    private readonly DateTimeOffset origin=DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow(){lock(sync)return origin.AddTicks(ticks);}
    public override long GetTimestamp(){lock(sync)return ticks;}
    public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
    {var timer=new Timer(this,callback,state);lock(sync)timers.Add(timer);timer.Change(dueTime,period);return timer;}
    public void Advance(TimeSpan by)
    {
        List<Timer> due;
        lock(sync){ticks+=by.Ticks;due=timers.Where(t=>!t.Closed&&t.Due<=ticks).ToList();foreach(var t in due)t.Due=t.Period>0?ticks+t.Period:long.MaxValue;}
        foreach(var timer in due)timer.Callback(timer.State);
    }
    private sealed class Timer(ManualPipelineClock owner,TimerCallback callback,object? state):ITimer
    {
        public readonly TimerCallback Callback=callback;public readonly object? State=state;
        public long Due=long.MaxValue,Period;public bool Closed;
        public bool Change(TimeSpan dueTime,TimeSpan period)
        {lock(owner.sync){if(Closed)return false;Due=dueTime==Timeout.InfiniteTimeSpan?long.MaxValue:owner.ticks+dueTime.Ticks;Period=period==Timeout.InfiniteTimeSpan?0:period.Ticks;return true;}}
        public void Dispose(){lock(owner.sync){Closed=true;Due=long.MaxValue;}}
        public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
    }
    public async Task Drive(Task task,int maximumSeconds=150)
    {
        for(var tick=0;tick<maximumSeconds*10&&!task.IsCompleted;tick++){Advance(TimeSpan.FromMilliseconds(100));await Task.Delay(1);}
        await task.WaitAsync(TimeSpan.FromSeconds(8));
    }
}
