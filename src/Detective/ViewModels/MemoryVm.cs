using CommunityToolkit.Mvvm.ComponentModel;
using Detective.Core;

namespace Detective.ViewModels;

public sealed partial class MemoryVm : FocusItemVm
{
    public MemoryVm() : base(FocusKind.Memory, "memory", Accents.Memory)
    {
        Title = "Memory";
        TileTitle = "Memory";
    }

    [ObservableProperty]
    private MemorySample? _composition;

    [ObservableProperty]
    private string _totalLabel = "";

    public void Apply(MemorySample s)
    {
        double percent = s.Total == 0 ? 0 : 100.0 * s.InUse / s.Total;
        TileSeries.Add((float)percent);
        Composition = s;
        TotalLabel = Format.Bytes(s.Total);
        TileSubtitle = $"{Format.Bytes(s.InUse)}/{Format.Bytes(s.Total)} ({Format.Percent(percent)})";

        Set(LiveStats, "In use", Format.Bytes(s.InUse));
        Set(LiveStats, "Available", Format.Bytes(s.Available));
        Set(LiveStats, "Committed", $"{Format.Bytes(s.Committed)}/{Format.Bytes(s.CommitLimit)}");
        Set(LiveStats, "Cached", Format.Bytes(s.Cached));
        Set(LiveStats, "Paged pool", Format.Bytes(s.PagedPool));
        Set(LiveStats, "Non-paged pool", Format.Bytes(s.NonPagedPool));

        if (Subtitle == "" && s.Installed > 0) Subtitle = Format.Bytes(s.Installed);
        _hardwareReserved = s.HardwareReserved;
        if (_info is not null && !_reservedShown) ShowStatic();
    }

    private MemoryInfo? _info;
    private ulong _hardwareReserved;
    private bool _reservedShown;

    public override async Task LoadStaticAsync()
    {
        _info = await Background(MemoryInfoReader.Load);
        if (_info is not null) ShowStatic();
    }

    private void ShowStatic()
    {
        var info = _info!;
        var first = info.Modules.FirstOrDefault();
        ulong installed = (ulong)info.Modules.Sum(m => (long)m.Capacity);
        if (installed > 0)
            Subtitle = $"{Format.Bytes(installed)} {first?.Type}".Trim();

        var rows = new List<(string, string)>
        {
            ("Speed", first is null ? "" : $"{first.ConfiguredSpeed} MT/s" + (first.Speed > first.ConfiguredSpeed ? $" (rated {first.Speed} MT/s)" : "")),
            ("Slots used", $"{info.Modules.Count} of {info.TotalSlots}"),
            ("Form factor", first?.FormFactor ?? ""),
            ("Type", first?.Type ?? ""),
            ("Voltage", first is { VoltageMv: > 0 } ? $"{first.VoltageMv / 1000.0:0.00} V" : ""),
            ("Timings", "Unavailable (needs SPD access via a kernel driver)"),
            ("Hardware reserved", _hardwareReserved > 0 ? Format.Bytes(_hardwareReserved) : ""),
        };
        foreach (var m in info.Modules)
            rows.Add((m.Slot, $"{Format.Bytes(m.Capacity)} · {m.Manufacturer} {m.PartNumber}".Trim()));

        Replace(StaticInfo, rows);
        _reservedShown = _hardwareReserved > 0;
    }
}
