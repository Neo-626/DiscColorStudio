using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
namespace DiscColorStudio;

// Cooperative work sharing: a block worker waiting for a shared tile computes
// queued rows instead of blocking in Lazy.Value. No additional CPU threads.
internal sealed class LadderWorkQueue(CancellationToken cancellation,int workers):IDisposable
{
    private readonly ConcurrentQueue<Action> jobs=new();
    private readonly object gate=new();
    private readonly CancellationTokenSource stopped=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
    private ExceptionDispatchInfo? failure;
    internal CancellationToken Token=>stopped.Token;
    internal void Check(){Volatile.Read(ref failure)?.Throw();Token.ThrowIfCancellationRequested();}
    internal void Wake(){lock(gate)Monitor.PulseAll(gate);}
    internal void Fail(Exception error)
    {
        Interlocked.CompareExchange(ref failure,ExceptionDispatchInfo.Capture(error),null);
        stopped.Cancel();Wake();
    }
    internal void Assist(Func<bool>? complete=null)
    {
        Check();
        if(jobs.TryDequeue(out var job))
        {try{job();}catch(Exception ex){Fail(ex);throw;}}
        else lock(gate){if(jobs.IsEmpty&&!(complete?.Invoke()??false)){Check();Monitor.Wait(gate,10);}}
    }
    internal void Until(Func<bool> complete)
    {
        while(!complete()){Check();Assist(complete);}
        Check();
    }
    internal void ForRows(int count,Action<int> row)=>Schedule(count,row,8);
    internal void ForColors(int count,Action<int> row)=>Schedule(count,row,1);
    private void Schedule(int count,Action<int> row,int chunk)
    {
        if(workers==1){for(int i=0;i<count;i++){Check();row(i);}return;}
        var group=new WorkGroup(this,(count+chunk-1)/chunk);
        try
        {
            for(int start=0;start<count;start+=chunk)
            {
                int from=start,to=Math.Min(count,start+chunk);
                jobs.Enqueue(()=>group.Run(()=>{for(int i=from;i<to;i++){Check();row(i);}}));
            }
            Wake();Until(()=>group.Complete);
        }
        catch
        {
            // Render callers unlock/dispose bitmap memory on return. Join all
            // active chunks before propagating failure; queued chunks become no-ops.
            group.CloseAndWait();throw;
        }
    }
    private sealed class WorkGroup(LadderWorkQueue owner,int remaining)
    {
        private readonly object sync=new();
        private int active;private bool closed;
        internal bool Complete=>Volatile.Read(ref remaining)==0;
        internal void Run(Action action)
        {
            lock(sync){if(closed)return;active++;}
            try{action();}
            catch(Exception ex){owner.Fail(ex);throw;}
            finally
            {
                lock(sync){active--;Interlocked.Decrement(ref remaining);Monitor.PulseAll(sync);}
                owner.Wake();
            }
        }
        internal void CloseAndWait()
        {lock(sync){closed=true;while(active!=0)Monitor.Wait(sync);}}
    }
    public void Dispose(){stopped.Dispose();jobs.Clear();}
}
