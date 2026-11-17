namespace DiscColorStudio;

// One worker budget for export: block workers share queued fitting rows.
// The OS schedules these workers across the processors available to the process.
internal sealed record LadderParallelPlan(int Workers,int CachedTiles)
{
    internal static LadderParallelPlan Current()
    {
        var memory=GC.GetGCMemoryInfo();
        long available=memory.TotalAvailableMemoryBytes;
        if(memory.MemoryLoadBytes>0)available=Math.Max(64L*1024*1024,available-memory.MemoryLoadBytes);
        return Create(Environment.ProcessorCount,available);
    }
    internal static LadderParallelPlan Create(int processors,long availableBytes)
    {
        // Reserve headroom for live tile/source arrays and per-worker last tiles.
        // Cache size affects recomputation only; the 128-cell sampling stays fixed.
        long memory=Math.Max(32L*1024*1024,availableBytes);
        int workers=(int)Math.Clamp(Math.Min(processors,memory/(32L*1024*1024)),1,64);
        int tiles=(int)Math.Clamp(memory/8/(128L*128*17),64,1536);
        return new(workers,tiles);
    }
}
