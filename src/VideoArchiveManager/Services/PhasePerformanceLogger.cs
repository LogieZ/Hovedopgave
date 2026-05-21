using System.Diagnostics;
using Serilog;

namespace VideoArchiveManager.Services;

public static class PhasePerformanceLogger
{
    public static async Task MeasureAsync(string phaseName, Func<Task> action)
    {
        var start = CaptureSnapshot();
        var stopwatch = Stopwatch.StartNew();

        Log.Information(
            "Phase '{PhaseName}' started | CPU={CpuMs} ms | WorkingSet={WorkingSetMb:N2} MB | PrivateMemory={PrivateMemoryMb:N2} MB | ManagedHeap={ManagedHeapMb:N2} MB",
            phaseName,
            start.CpuTime.TotalMilliseconds,
            BytesToMegabytes(start.WorkingSetBytes),
            BytesToMegabytes(start.PrivateMemoryBytes),
            BytesToMegabytes(start.ManagedHeapBytes));

        try
        {
            await action();
            LogCompletion(phaseName, stopwatch.Elapsed, start, CaptureSnapshot(), failed: false);
        }
        catch
        {
            LogCompletion(phaseName, stopwatch.Elapsed, start, CaptureSnapshot(), failed: true);
            throw;
        }
    }

    public static async Task<T> MeasureAsync<T>(string phaseName, Func<Task<T>> action)
    {
        var start = CaptureSnapshot();
        var stopwatch = Stopwatch.StartNew();

        Log.Information(
            "Phase '{PhaseName}' started | CPU={CpuMs} ms | WorkingSet={WorkingSetMb:N2} MB | PrivateMemory={PrivateMemoryMb:N2} MB | ManagedHeap={ManagedHeapMb:N2} MB",
            phaseName,
            start.CpuTime.TotalMilliseconds,
            BytesToMegabytes(start.WorkingSetBytes),
            BytesToMegabytes(start.PrivateMemoryBytes),
            BytesToMegabytes(start.ManagedHeapBytes));

        try
        {
            var result = await action();
            LogCompletion(phaseName, stopwatch.Elapsed, start, CaptureSnapshot(), failed: false);
            return result;
        }
        catch
        {
            LogCompletion(phaseName, stopwatch.Elapsed, start, CaptureSnapshot(), failed: true);
            throw;
        }
    }

    private static void LogCompletion(string phaseName, TimeSpan elapsed, PerformanceSnapshot start, PerformanceSnapshot end, bool failed)
    {
        if (failed)
        {
            Log.Error(
                "Phase '{PhaseName}' {Outcome} | Elapsed={ElapsedMs} ms | CPU delta={CpuDeltaMs} ms | WorkingSet={WorkingSetMb:N2} MB (delta {WorkingSetDeltaMb:+0.00;-0.00;0.00} MB) | PrivateMemory={PrivateMemoryMb:N2} MB (delta {PrivateMemoryDeltaMb:+0.00;-0.00;0.00} MB) | ManagedHeap={ManagedHeapMb:N2} MB (delta {ManagedHeapDeltaMb:+0.00;-0.00;0.00} MB)",
                phaseName,
                "failed",
                elapsed.TotalMilliseconds,
                (end.CpuTime - start.CpuTime).TotalMilliseconds,
                BytesToMegabytes(end.WorkingSetBytes),
                BytesToMegabytes(end.WorkingSetBytes - start.WorkingSetBytes),
                BytesToMegabytes(end.PrivateMemoryBytes),
                BytesToMegabytes(end.PrivateMemoryBytes - start.PrivateMemoryBytes),
                BytesToMegabytes(end.ManagedHeapBytes),
                BytesToMegabytes(end.ManagedHeapBytes - start.ManagedHeapBytes));

            return;
        }

        Log.Information(
            "Phase '{PhaseName}' {Outcome} | Elapsed={ElapsedMs} ms | CPU delta={CpuDeltaMs} ms | WorkingSet={WorkingSetMb:N2} MB (delta {WorkingSetDeltaMb:+0.00;-0.00;0.00} MB) | PrivateMemory={PrivateMemoryMb:N2} MB (delta {PrivateMemoryDeltaMb:+0.00;-0.00;0.00} MB) | ManagedHeap={ManagedHeapMb:N2} MB (delta {ManagedHeapDeltaMb:+0.00;-0.00;0.00} MB)",
            phaseName,
            "completed",
            elapsed.TotalMilliseconds,
            (end.CpuTime - start.CpuTime).TotalMilliseconds,
            BytesToMegabytes(end.WorkingSetBytes),
            BytesToMegabytes(end.WorkingSetBytes - start.WorkingSetBytes),
            BytesToMegabytes(end.PrivateMemoryBytes),
            BytesToMegabytes(end.PrivateMemoryBytes - start.PrivateMemoryBytes),
            BytesToMegabytes(end.ManagedHeapBytes),
            BytesToMegabytes(end.ManagedHeapBytes - start.ManagedHeapBytes));
    }

    private static PerformanceSnapshot CaptureSnapshot()
    {
        var process = Process.GetCurrentProcess();

        return new PerformanceSnapshot(
            process.TotalProcessorTime,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.GetTotalMemory(forceFullCollection: false));
    }

    private static double BytesToMegabytes(long bytes) => bytes / 1024d / 1024d;

    private readonly record struct PerformanceSnapshot(
        TimeSpan CpuTime,
        long WorkingSetBytes,
        long PrivateMemoryBytes,
        long ManagedHeapBytes);
}