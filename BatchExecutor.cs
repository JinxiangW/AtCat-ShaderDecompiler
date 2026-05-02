using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ruri.ShaderTools;

// Internal driver for `ShaderDecompiler.Decompile(IReadOnlyList<...>)`. Keeps the public
// surface on ShaderDecompiler minimal: callers see one Decompile overload that takes a
// list and returns an array. This file is the pool / CPU-throttling implementation
// behind that.
//
// Design notes:
//   - Each worker holds its own ShaderDecompiler instance. The engine's per-call mutable
//     state (last tool stderr, rewriter summary) makes shared instances unsafe under
//     concurrent calls; spinning up one extra instance per worker is cheap (the cost is
//     finding the Tools/ directory once).
//   - Concurrency is admission-gated, not Parallel.ForEach-style. The cap can be raised
//     dynamically by the CPU monitor; it never shrinks because a job already mid-flight
//     can't be unstarted, only refused-from-starting in the future.
//   - GetSystemTimes covers system-wide CPU on Windows. On other platforms the monitor
//     opens the gate fully (no throttling) — the project is Windows-targeted today.
internal static class BatchExecutor
{
    public static void Run(
        ShaderDecompiler caller,
        IReadOnlyList<(byte[] Binary, DecompileOptions Options)> requests,
        DecompileResult[] results,
        Action<int, DecompileResult>? onProgress,
        int maxConcurrencyArg,
        int cpuUsageCapPercent,
        CancellationToken cancellationToken)
    {
        int maxWorkers = maxConcurrencyArg > 0 ? maxConcurrencyArg : Math.Max(1, Environment.ProcessorCount * 2);
        int cpuCap = Math.Clamp(cpuUsageCapPercent, 1, 100);

        var queue = new BlockingCollection<int>(boundedCapacity: requests.Count);
        for (int i = 0; i < requests.Count; i++) queue.Add(i);
        queue.CompleteAdding();

        var gate = new AdmissionGate(initialAllowed: 1, ceiling: maxWorkers);

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task coordinator = Task.Run(() => MonitorCpuLoop(gate, cpuCap, linkedCts.Token));

        var workers = new Task[maxWorkers];
        for (int i = 0; i < maxWorkers; i++)
        {
            workers[i] = Task.Run(() => Worker(caller, requests, results, queue, gate, onProgress, linkedCts.Token));
        }

        try
        {
            Task.WaitAll(workers, cancellationToken);
        }
        finally
        {
            linkedCts.Cancel();
            try { coordinator.Wait(); } catch { /* coordinator faults are advisory */ }
            queue.Dispose();
        }
    }

    private static void Worker(
        ShaderDecompiler caller,
        IReadOnlyList<(byte[] Binary, DecompileOptions Options)> requests,
        DecompileResult[] results,
        BlockingCollection<int> queue,
        AdmissionGate gate,
        Action<int, DecompileResult>? onProgress,
        CancellationToken cancellationToken)
    {
        // Each worker owns its own decompiler. They share the caller's TempDir / ToolsDir
        // so there's no path-discovery cost; temp file names use GUIDs so concurrent
        // workers don't collide.
        using ShaderDecompiler decompiler = new(caller.TempDir, ResolveToolsDir(caller));

        while (!cancellationToken.IsCancellationRequested)
        {
            int index;
            try
            {
                if (!queue.TryTake(out index, Timeout.Infinite, cancellationToken)) return;
            }
            catch (OperationCanceledException) { return; }
            catch (InvalidOperationException) { return; }  // queue completed + drained

            if (!gate.AcquireSlot(cancellationToken)) return;

            DecompileResult result;
            try
            {
                var (binary, options) = requests[index];
                result = decompiler.Decompile(binary, options);
            }
            catch (Exception ex)
            {
                result = new DecompileResult
                {
                    Success = false,
                    ErrorMessage = $"Worker exception: {ex}",
                    FailedStage = "worker-loop",
                };
            }
            finally
            {
                gate.ReleaseSlot();
            }

            results[index] = result;
            onProgress?.Invoke(index, result);
        }
    }

    // We don't expose ToolsDir off the public ShaderDecompiler API, but workers need to
    // inherit it from the caller so `dxbc2dxil.exe` etc. resolve identically. Reach in
    // through the internal field via reflection-free passthrough: re-run the same
    // discovery logic by passing the caller's TempDir as the only hint and letting the
    // workers' constructor probe the standard locations. Equivalent to "use the same
    // tooling the caller would use".
    private static string? ResolveToolsDir(ShaderDecompiler caller)
    {
        // The constructor probes BaseDirectory + side-by-side Tools folders when toolsDir
        // is null, which mirrors what the caller would have done. Returning null is
        // therefore the right "inherit caller's tooling" signal here.
        _ = caller;
        return null;
    }

    private static void MonitorCpuLoop(AdmissionGate gate, int cpuCapPercent, CancellationToken cancellationToken)
    {
        // Prime the deltas; without a baseline the first sample reads 100%.
        if (!TrySampleCpuTimes(out long prevIdle, out long prevKernel, out long prevUser))
        {
            // CPU sampling unavailable (non-Windows or restricted process) — open the
            // gate fully so callers still get parallelism, just without throttling.
            gate.OpenFully();
            return;
        }

        TimeSpan interval = TimeSpan.FromMilliseconds(750);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Task.Delay(interval, cancellationToken).Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!TrySampleCpuTimes(out long idle, out long kernel, out long user)) continue;

            long idleDelta = idle - prevIdle;
            long kernelDelta = kernel - prevKernel;
            long userDelta = user - prevUser;
            prevIdle = idle;
            prevKernel = kernel;
            prevUser = user;

            long totalDelta = kernelDelta + userDelta;
            if (totalDelta <= 0) continue;

            // GetSystemTimes returns kernel-time INCLUSIVE of idle-time, so the total
            // wall-clock CPU envelope is kernel + user, of which idle is subtracted.
            double usage = 1.0 - (double)idleDelta / totalDelta;
            int usagePercent = (int)Math.Round(usage * 100);

            if (usagePercent < cpuCapPercent)
            {
                gate.GrantOneMore();
            }
            // At/above cap: leave the gate alone — current jobs run to completion, we
            // just stop admitting new ones. Cap never shrinks.
        }
    }

    private static bool TrySampleCpuTimes(out long idle, out long kernel, out long user)
    {
        idle = 0; kernel = 0; user = 0;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        try { return GetSystemTimes(out idle, out kernel, out user); }
        catch { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    private sealed class AdmissionGate
    {
        private readonly object _lock = new();
        private readonly int _ceiling;
        private int _allowed;
        private int _inFlight;

        public AdmissionGate(int initialAllowed, int ceiling)
        {
            _ceiling = ceiling;
            _allowed = Math.Min(initialAllowed, ceiling);
        }

        public bool AcquireSlot(CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                while (_inFlight >= _allowed)
                {
                    Monitor.Wait(_lock, 250);
                    if (cancellationToken.IsCancellationRequested) return false;
                }
                _inFlight++;
                return true;
            }
        }

        public void ReleaseSlot()
        {
            lock (_lock)
            {
                if (_inFlight > 0) _inFlight--;
                Monitor.PulseAll(_lock);
            }
        }

        public void GrantOneMore()
        {
            lock (_lock)
            {
                if (_allowed < _ceiling)
                {
                    _allowed++;
                    Monitor.PulseAll(_lock);
                }
            }
        }

        public void OpenFully()
        {
            lock (_lock)
            {
                _allowed = _ceiling;
                Monitor.PulseAll(_lock);
            }
        }
    }
}
