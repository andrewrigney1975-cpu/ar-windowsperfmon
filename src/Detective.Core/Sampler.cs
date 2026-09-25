using System.Diagnostics;

namespace Detective.Core;

public sealed record Snapshot(
    CpuSample? Cpu,
    MemorySample? Memory,
    IReadOnlyList<DiskSample> Disks,
    IReadOnlyList<NetworkSample> Networks,
    IReadOnlyList<GpuSample> Gpus);

/// <summary>
/// Collects every monitor on a background loop at one shared interval and raises <see cref="Sampled"/>
/// (on the background thread) with an immutable snapshot.
/// </summary>
public sealed class Sampler : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private PeriodicTimer? _timer;
    private TimeSpan _interval;
    private Task? _loop;

    public Sampler(TimeSpan interval) => _interval = interval;

    public event Action<Snapshot>? Sampled;

    /// <summary>Raised once a monitor throws; the loop keeps running with that monitor's data left empty.</summary>
    public event Action<string, Exception>? MonitorFailed;

    public bool Paused { get; set; }

    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            _interval = value;
            if (_timer is { } t) t.Period = value;
        }
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        // Monitors are created here, not in the constructor: first-time PDH and DXGI setup can take a few hundred ms.
        using var cpu = Create(() => new CpuMonitor());
        using var memory = Create(() => new MemoryMonitor());
        using var disk = Create(() => new DiskMonitor());
        using var network = Create(() => new NetworkMonitor());
        using var gpu = Create(() => new GpuMonitor());

        using var timer = new PeriodicTimer(_interval);
        _timer = timer;

        // Rate counters need a baseline, so the first real sample is one interval after start.
        Collect(cpu, memory, disk, network, gpu);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (Paused) continue;
                var snapshot = Collect(cpu, memory, disk, network, gpu);
                Sampled?.Invoke(snapshot);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _timer = null;
        }
    }

    private Snapshot Collect(CpuMonitor? cpu, MemoryMonitor? memory, DiskMonitor? disk, NetworkMonitor? network, GpuMonitor? gpu) =>
        new(
            Try("CPU", () => cpu?.Sample()),
            Try("Memory", () => memory?.Sample()),
            Try("Disk", () => disk?.Sample()) ?? [],
            Try("Network", () => network?.Sample()) ?? [],
            Try("GPU", () => gpu?.Sample()) ?? []);

    private readonly HashSet<string> _reported = new();

    private T? Try<T>(string name, Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception ex)
        {
            if (_reported.Add(name)) MonitorFailed?.Invoke(name, ex);
            Debug.WriteLine($"{name} monitor failed: {ex}");
            return null;
        }
    }

    private T? Create<T>(Func<T> factory) where T : class
    {
        try { return factory(); }
        catch (Exception ex)
        {
            MonitorFailed?.Invoke(typeof(T).Name, ex);
            return null;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
