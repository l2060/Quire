namespace Quire.Cli.Build;

internal sealed record BuildOptions
{
    internal int Jobs { get; init; }
    internal int IoConcurrency { get; init; } = 4;
    internal int WriteConcurrency { get; init; } = 1;
    internal int MemoryMiB { get; init; } = 128;
    internal int BlockSize { get; init; } = 256 * 1024;
    internal int EffectiveJobs
    {
        get
        {
            if (Jobs != 0)
                return Jobs;
            // Each worker can hold two queued blocks, one input and two buffers
            // whose compressed/encrypted capacities round up to two pool blocks each.
            long budgetJobs = BlockSize > 0 ? ((long)MemoryMiB * 1024 * 1024 / BlockSize - IoConcurrency - 4) / 7 : 0;
            return (int)Math.Min(Math.Min(Environment.ProcessorCount, 256), Math.Max(1, budgetJobs));
        }
    }

    internal void Validate()
    {
        if (Jobs is < 0 or > 256 || IoConcurrency is < 1 or > 256 || WriteConcurrency is < 1 or > 64 || MemoryMiB is < 8 or > 65536 ||
            BlockSize is < 65536 or > 1048576 || (BlockSize & (BlockSize - 1)) != 0)
            throw new ArgumentException("Invalid build concurrency, memory or block-size setting.");
        if (((long)EffectiveJobs * 7 + IoConcurrency + 4) * BlockSize > (long)MemoryMiB * 1024 * 1024)
            throw new ArgumentException("Memory budget is too small for the selected jobs, I/O concurrency and block size.");
    }
}
