using CommunityToolkit.Mvvm.ComponentModel;
using Detective.Core;

namespace Detective.ViewModels;

public sealed partial class CpuVm : FocusItemVm
{
    public CpuVm() : base(FocusKind.Cpu, "cpu", Accents.Cpu)
    {
        Title = "CPU";
        TileTitle = "CPU";
    }

    /// <summary>One history per logical processor, in group/index order.</summary>
    public List<RingBuffer> Logical { get; } = new();

    /// <summary>Raised when the logical-processor count first becomes known (or changes).</summary>
    public event EventHandler? LogicalChanged;

    [ObservableProperty]
    private bool _showLogical;

    public void Apply(CpuSample s)
    {
        TileSeries.Add((float)s.Utilization);

        if (s.PerLogical.Length > 0 && s.PerLogical.Length != Logical.Count)
        {
            Logical.Clear();
            for (int i = 0; i < s.PerLogical.Length; i++) Logical.Add(new RingBuffer());
            LogicalChanged?.Invoke(this, EventArgs.Empty);
        }
        for (int i = 0; i < Logical.Count && i < s.PerLogical.Length; i++)
            Logical[i].Add((float)s.PerLogical[i]);

        string speed = s.SpeedMhz > 0 ? Format.Mhz(s.SpeedMhz) : "—";
        TileSubtitle = $"{Format.Percent(s.Utilization)}  {speed}";

        Set(LiveStats, "Utilization", Format.Percent(s.Utilization));
        Set(LiveStats, "Speed", speed);
        Set(LiveStats, "Processes", Format.Count(s.Processes));
        Set(LiveStats, "Threads", Format.Count(s.Threads));
        Set(LiveStats, "Handles", Format.Count(s.Handles));
        Set(LiveStats, "Up time", Format.Uptime(s.Uptime));
    }

    public override async Task LoadStaticAsync()
    {
        if (await Background(CpuInfoReader.Load) is not { } info) return;
        Subtitle = info.Name;
        Replace(StaticInfo,
        [
            ("Manufacturer", info.Vendor),
            ("Base speed", info.BaseMhz > 0 ? Format.Mhz(info.BaseMhz) : ""),
            ("Sockets", info.Sockets.ToString()),
            ("Cores", info.Cores.ToString()),
            ("Logical processors", info.LogicalProcessors.ToString()),
            ("Virtualization", info.Virtualization ? "Enabled" : "Disabled"),
            ("Hypervisor", info.HypervisorPresent ? "Running" : ""),
            ("L1 cache", info.L1Cache > 0 ? Format.Bytes(info.L1Cache) : ""),
            ("L2 cache", info.L2Cache > 0 ? Format.Bytes(info.L2Cache) : ""),
            ("L3 cache", info.L3Cache > 0 ? Format.Bytes(info.L3Cache) : ""),
            ("Family", info.Family),
            ("Model", info.Model),
            ("Stepping", info.Stepping),
        ]);
    }
}
