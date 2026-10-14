using System.Diagnostics;
using Detective.Core.Native;

namespace Detective.Core;

/// <summary>Identity that survives PID reuse: Windows recycles PIDs, but never with the same creation time.</summary>
public readonly record struct ProcessKey(int Pid, long CreateTime);

public sealed record ProcessSample(
    ProcessKey Key,
    int ParentPid,
    string Name,
    string? Description,
    string? Path,
    double Cpu,
    ulong Memory,
    double DiskBytesPerSec,
    double? NetworkBitsPerSec,
    double Gpu,
    bool HasWindow)
{
    public int Pid => Key.Pid;

    /// <summary>Task Manager's display name: the file description when there is one.</summary>
    public string DisplayName => string.IsNullOrEmpty(Description) ? Name : Description;
}

public sealed record ProcessList(
    IReadOnlyList<ProcessSample> Items,
    bool NetworkAvailable,
    ulong TotalPhysicalMemory,
    uint MemoryLoad);

/// <summary>
/// Samples every process: CPU and disk from one NtQuerySystemInformation call, GPU from the GPU monitor's
/// engine counters, and network from ETW when elevated.
/// </summary>
public sealed class ProcessMonitor : IDisposable
{
    private readonly Dictionary<ProcessKey, (long CpuTime, ulong IoBytes)> _previous = new();
    private readonly Dictionary<ProcessKey, (string? Path, string? Description)> _info = new();
    private readonly Dictionary<string, string?> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = new();
    private readonly NetworkTrace? _network = NetworkTrace.TryStart();
    private readonly int _processors = (int)Math.Max(1, ProcessApi.GetActiveProcessorCount(ProcessApi.ALL_PROCESSOR_GROUPS));

    public ProcessMonitor() => ProcessApi.EnableDebugPrivilege();

    public bool NetworkAvailable => _network is not null;

    public ProcessList Sample(IReadOnlyDictionary<int, double>? gpuByPid)
    {
        var entries = Ntdll.ReadProcesses();
        double elapsed = _clock.IsRunning ? _clock.Elapsed.TotalSeconds : 0;
        _clock.Restart();
        var network = _network?.TakeBytes();
        var windows = AppWindows.ProcessIds();

        var samples = new List<ProcessSample>(entries.Count);
        var seen = new HashSet<ProcessKey>();
        foreach (var e in entries)
        {
            var key = new ProcessKey(e.Pid, e.CreateTime);
            seen.Add(key);
            ulong io = e.IoReadBytes + e.IoWriteBytes;
            double cpu = 0, disk = 0;
            if (elapsed > 0 && _previous.TryGetValue(key, out var prev))
            {
                cpu = CpuPercent(e.CpuTime - prev.CpuTime, elapsed, _processors);
                disk = io >= prev.IoBytes ? (io - prev.IoBytes) / elapsed : 0;
            }
            _previous[key] = (e.CpuTime, io);

            var (path, description) = Info(key, e.ImageName);
            double? net = network is null ? null
                : elapsed > 0 ? network.GetValueOrDefault(e.Pid) * 8 / elapsed : 0;

            samples.Add(new ProcessSample(
                key, e.ParentPid, e.ImageName, description, path, cpu, e.PrivateWorkingSet, disk, net,
                gpuByPid?.GetValueOrDefault(e.Pid) ?? 0, windows.Contains(e.Pid)));
        }

        Prune(_previous, seen);
        Prune(_info, seen);

        var memory = Kernel32.MemoryStatus();
        return new ProcessList(samples, NetworkAvailable, memory?.ullTotalPhys ?? 0, memory?.dwMemoryLoad ?? 0);
    }

    /// <summary>CPU time (100 ns units) used over the interval, as a share of every logical processor.</summary>
    public static double CpuPercent(long cpuTimeDelta, double elapsedSeconds, int processors) =>
        elapsedSeconds <= 0 || cpuTimeDelta <= 0
            ? 0
            : Math.Clamp(cpuTimeDelta / 1e7 / elapsedSeconds / processors * 100, 0, 100);

    /// <summary>Image path and description, looked up once per process (and once per file).</summary>
    private (string? Path, string? Description) Info(ProcessKey key, string imageName)
    {
        if (_info.TryGetValue(key, out var info)) return info;
        string? path = key.Pid is 0 or 4 ? null : ProcessApi.ImagePath(key.Pid);
        string? description = null;
        if (path is not null && !_descriptions.TryGetValue(path, out description))
        {
            description = ProcessApi.FileDescription(path);
            _descriptions[path] = description;
        }
        if (key.Pid == 4) description = "System";
        info = (path, description);
        _info[key] = info;
        return info;
    }

    private static void Prune<T>(Dictionary<ProcessKey, T> map, HashSet<ProcessKey> keep)
    {
        if (map.Count == keep.Count) return;
        foreach (var k in map.Keys.Where(k => !keep.Contains(k)).ToList()) map.Remove(k);
    }

    public void Dispose() => _network?.Dispose();
}

/// <summary>A process with the processes it started, as grouped for display.</summary>
public sealed class ProcessNode(ProcessSample sample)
{
    public ProcessSample Sample { get; } = sample;

    public List<ProcessNode> Children { get; } = new();
}

/// <summary>Parent/child relationships and Task Manager's Apps / Background grouping.</summary>
public static class ProcessTree
{
    /// <summary>
    /// A parent link counts only if the parent is still running and started before the child. Otherwise the
    /// recorded parent PID may belong to a newer, unrelated process that reused the number.
    /// </summary>
    public static bool IsParent(ProcessKey parent, ProcessKey child) =>
        parent.Pid != child.Pid && parent.CreateTime <= child.CreateTime;

    /// <summary>Child lists keyed by parent PID, holding only valid links.</summary>
    public static Dictionary<int, List<T>> Children<T>(IReadOnlyList<T> items, Func<T, ProcessKey> key, Func<T, int> parentPid)
    {
        var byPid = new Dictionary<int, ProcessKey>(items.Count);
        foreach (var item in items) byPid[key(item).Pid] = key(item);

        var children = new Dictionary<int, List<T>>();
        foreach (var item in items)
        {
            if (!byPid.TryGetValue(parentPid(item), out var parent) || !IsParent(parent, key(item))) continue;
            if (!children.TryGetValue(parent.Pid, out var list)) children[parent.Pid] = list = new List<T>();
            list.Add(item);
        }
        return children;
    }

    /// <summary>
    /// Apps: each process with an app window, with the processes it started beneath it (a descendant that
    /// has its own window becomes an app of its own). Background: every other process, as a parent/child tree.
    /// </summary>
    public static (List<ProcessNode> Apps, List<ProcessNode> Background) Group(IReadOnlyList<ProcessSample> samples)
    {
        var children = Children(samples, s => s.Key, s => s.ParentPid);
        var inApp = new HashSet<int>();
        var apps = new List<ProcessNode>();

        ProcessNode Build(ProcessSample sample, Func<ProcessSample, bool> include)
        {
            var node = new ProcessNode(sample);
            if (children.TryGetValue(sample.Pid, out var kids))
                foreach (var kid in kids)
                    if (include(kid))
                        node.Children.Add(Build(kid, include));
            return node;
        }

        foreach (var s in samples.Where(s => s.HasWindow))
        {
            apps.Add(Build(s, kid => !kid.HasWindow));
        }
        foreach (var app in apps) Mark(app, inApp);

        var background = new List<ProcessNode>();
        var backgroundPids = samples.Where(s => !inApp.Contains(s.Pid)).Select(s => s.Pid).ToHashSet();
        foreach (var s in samples)
        {
            if (!backgroundPids.Contains(s.Pid)) continue;
            bool hasBackgroundParent = backgroundPids.Contains(s.ParentPid)
                && children.TryGetValue(s.ParentPid, out var siblings) && siblings.Contains(s);
            if (!hasBackgroundParent) background.Add(Build(s, kid => backgroundPids.Contains(kid.Pid)));
        }
        return (apps, background);
    }

    private static void Mark(ProcessNode node, HashSet<int> set)
    {
        set.Add(node.Sample.Pid);
        foreach (var c in node.Children) Mark(c, set);
    }

    /// <summary>The process and all its descendants, parent first, then children deepest-first.</summary>
    public static List<ProcessKey> TreeOf(IReadOnlyList<ProcessEntry> entries, ProcessKey root)
    {
        var children = Children(entries, e => new ProcessKey(e.Pid, e.CreateTime), e => e.ParentPid);
        var levels = new List<List<ProcessKey>> { new() { root } };
        var visited = new HashSet<int> { root.Pid };
        while (true)
        {
            var next = new List<ProcessKey>();
            foreach (var p in levels[^1])
                if (children.TryGetValue(p.Pid, out var kids))
                    foreach (var k in kids)
                        if (visited.Add(k.Pid)) next.Add(new ProcessKey(k.Pid, k.CreateTime));
            if (next.Count == 0) break;
            levels.Add(next);
        }
        // The root goes first so it can't start replacements; then leaves upwards so no child is orphaned mid-way.
        var order = new List<ProcessKey> { root };
        for (int i = levels.Count - 1; i >= 1; i--) order.AddRange(levels[i]);
        return order;
    }
}

public enum KillOutcome
{
    Ended,
    AlreadyExited,
    AccessDenied,
    Protected,
    Failed,
}

public sealed record KillResult(int Requested, int Ended, IReadOnlyList<(ProcessKey Key, KillOutcome Outcome)> Failures);

/// <summary>Ends a process or a process tree, checking identity so a reused PID is never hit.</summary>
public static class ProcessKiller
{
    /// <summary>Why this process may not be ended from Detective, or null if it may.</summary>
    public static string? Forbidden(int pid) => pid switch
    {
        0 => "The System Idle Process isn't a real process.",
        4 => "The System process can't be ended.",
        _ when pid == Environment.ProcessId => "Detective can't end itself.",
        _ => null,
    };

    /// <summary>Descendants of a process as currently running (for the confirmation prompt).</summary>
    public static List<ProcessKey> Tree(ProcessKey root) => ProcessTree.TreeOf(Ntdll.ReadProcesses(), root);

    public static KillResult Kill(ProcessKey key)
    {
        var outcome = KillOne(key);
        return new KillResult(1, outcome == KillOutcome.Ended ? 1 : 0, outcome == KillOutcome.Ended ? [] : [(key, outcome)]);
    }

    /// <summary>Ends the tree; then looks once more for children started while it was being ended.</summary>
    public static KillResult KillTree(ProcessKey root)
    {
        var failures = new List<(ProcessKey, KillOutcome)>();
        var done = new HashSet<ProcessKey>();
        int ended = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            var entries = Ntdll.ReadProcesses();
            var targets = pass == 0
                ? ProcessTree.TreeOf(entries, root)
                : StragglersOf(entries, done);
            foreach (var key in targets)
            {
                // Detective may itself be a descendant (e.g. when ending Explorer's tree); it keeps running.
                if (key.Pid == Environment.ProcessId || !done.Add(key)) continue;
                var outcome = KillOne(key);
                if (outcome == KillOutcome.Ended) ended++;
                // A descendant that exits on its own as its parent goes isn't a failure.
                else if (outcome != KillOutcome.AlreadyExited || key == root) failures.Add((key, outcome));
            }
        }
        return new KillResult(done.Count, ended, failures);
    }

    private static IEnumerable<ProcessKey> StragglersOf(List<ProcessEntry> entries, HashSet<ProcessKey> ended)
    {
        var endedPids = ended.ToDictionary(k => k.Pid);
        foreach (var e in entries)
        {
            var key = new ProcessKey(e.Pid, e.CreateTime);
            if (!ended.Contains(key) && endedPids.TryGetValue(e.ParentPid, out var parent) && ProcessTree.IsParent(parent, key))
            {
                foreach (var k in ProcessTree.TreeOf(entries, key)) yield return k;
            }
        }
    }

    static ProcessKiller() => ProcessApi.EnableDebugPrivilege();

    private static KillOutcome KillOne(ProcessKey key)
    {
        if (Forbidden(key.Pid) is not null) return KillOutcome.Protected;
        IntPtr h = ProcessApi.OpenProcess(ProcessApi.PROCESS_TERMINATE | ProcessApi.PROCESS_QUERY_LIMITED_INFORMATION, false, key.Pid);
        if (h == IntPtr.Zero)
        {
            return System.Runtime.InteropServices.Marshal.GetLastPInvokeError() switch
            {
                ProcessApi.ERROR_ACCESS_DENIED => KillOutcome.AccessDenied,
                ProcessApi.ERROR_INVALID_PARAMETER => KillOutcome.AlreadyExited,
                _ => KillOutcome.Failed,
            };
        }
        try
        {
            if (!ProcessApi.GetProcessTimes(h, out long created, out _, out _, out _) || created != key.CreateTime)
                return KillOutcome.AlreadyExited;
            // Ending a critical process (csrss, wininit, ...) bugchecks the machine.
            if (ProcessApi.IsProcessCritical(h, out bool critical) && critical) return KillOutcome.Protected;
            if (ProcessApi.TerminateProcess(h, 1)) return KillOutcome.Ended;
            return System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == ProcessApi.ERROR_ACCESS_DENIED
                ? KillOutcome.AccessDenied
                : KillOutcome.Failed;
        }
        finally
        {
            ProcessApi.CloseHandle(h);
        }
    }
}
