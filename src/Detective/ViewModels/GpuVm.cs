using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Detective.Core;

namespace Detective.ViewModels;

/// <summary>One of the four selectable engine charts on the GPU view.</summary>
public sealed partial class EngineSlot : ObservableObject
{
    private static readonly RingBuffer Empty = new();
    private readonly GpuVm _owner;
    private string _selected;

    public EngineSlot(GpuVm owner, string initial)
    {
        _owner = owner;
        _selected = initial;
    }

    public NameCollection Options => _owner.EngineNames;

    /// <summary>Object-typed for ComboBox.SelectedItem; null (sent while ItemsSource swaps) is ignored.</summary>
    public object? Selected
    {
        get => _selected;
        set
        {
            if (value is not string s || s == _selected) return;
            _selected = s;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Series));
            OnPropertyChanged(nameof(Value));
        }
    }

    public string Name => _selected;

    public RingBuffer Series => _owner.History.GetValueOrDefault(_selected) ?? Empty;

    public string Value => Format.Percent(Series.Latest);

    internal void Refresh() => OnPropertyChanged(nameof(Value));
}

public sealed partial class GpuVm : FocusItemVm
{
    private static readonly string[] Defaults = ["3D", "Compute", "Video Decode", "Video Encode"];

    private readonly GpuAdapter _adapter;
    private bool _slotsResolved;

    public GpuVm(GpuAdapter adapter, string[]? savedEngines = null) : base(FocusKind.Gpu, adapter.LuidKey, Accents.Gpu)
    {
        _adapter = adapter;
        Title = $"GPU {adapter.Index}";
        TileTitle = Title;
        Subtitle = adapter.Name;

        // Saved picks win over the defaults; any the adapter lacks are swapped out once engines are known.
        var initial = savedEngines is { Length: 4 } ? savedEngines : Defaults;
        foreach (var name in initial.Concat(Defaults).Distinct()) EngineNames.Add(name);
        Slots = initial.Select(d => new EngineSlot(this, d)).ToArray();
        foreach (var slot in Slots)
            slot.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EngineSlot.Selected)) EnginesChanged?.Invoke(this, EventArgs.Empty);
            };
        ShowStatic(null);
    }

    /// <summary>The adapter's LUID changes every boot; index + name does not.</summary>
    public override string PersistKey => $"{_adapter.Index}:{_adapter.Name}";

    /// <summary>Raised when the user picks a different engine for any chart.</summary>
    public event EventHandler? EnginesChanged;

    public string[] SelectedEngines => Slots.Select(s => s.Name).ToArray();

    public NameCollection EngineNames { get; } = new();

    internal Dictionary<string, RingBuffer> History { get; } = new();

    public EngineSlot[] Slots { get; }

    public EngineSlot Slot0 => Slots[0];
    public EngineSlot Slot1 => Slots[1];
    public EngineSlot Slot2 => Slots[2];
    public EngineSlot Slot3 => Slots[3];

    /// <summary>Memory charts are plotted as percent of capacity.</summary>
    public RingBuffer Dedicated { get; } = new();

    public RingBuffer Shared { get; } = new();

    [ObservableProperty]
    private string _dedicatedLabel = "";

    [ObservableProperty]
    private string _sharedLabel = "";

    public void Apply(GpuSample s)
    {
        TileSeries.Add((float)s.Utilization);

        foreach (var (name, value) in s.Engines)
        {
            if (!History.TryGetValue(name, out var ring))
            {
                ring = new RingBuffer();
                // Back-fill so a newly seen engine lines up with the rest of the history.
                for (int i = 0; i < TileSeries.Count - 1; i++) ring.Add(0);
                History[name] = ring;
                if (!EngineNames.Contains(name)) EngineNames.Add(name);
            }
            ring.Add((float)value);
        }
        foreach (var (name, ring) in History)
            if (!s.Engines.ContainsKey(name)) ring.Add(0);

        if (!_slotsResolved && s.Engines.Count > 0) ResolveSlots();
        foreach (var slot in Slots) slot.Refresh();

        double dedicatedTotal = _adapter.DedicatedVideoMemory;
        double sharedTotal = _adapter.SharedSystemMemory;
        Dedicated.Add(dedicatedTotal > 0 ? (float)(100.0 * s.DedicatedUsed / dedicatedTotal) : 0);
        Shared.Add(sharedTotal > 0 ? (float)(100.0 * s.SharedUsed / sharedTotal) : 0);
        DedicatedLabel = Format.Bytes(dedicatedTotal);
        SharedLabel = Format.Bytes(sharedTotal);

        string temperature = s.TemperatureC is { } t ? $"  ({t:0} °C)" : "";
        TileSubtitle = $"{Format.Percent(s.Utilization)}{temperature}";

        Set(LiveStats, "Utilization", Format.Percent(s.Utilization));
        Set(LiveStats, "Dedicated GPU memory", $"{Format.Bytes(s.DedicatedUsed)}/{Format.Bytes(dedicatedTotal)}");
        Set(LiveStats, "Shared GPU memory", $"{Format.Bytes(s.SharedUsed)}/{Format.Bytes(sharedTotal)}");
        Set(LiveStats, "GPU memory", $"{Format.Bytes(s.DedicatedUsed + s.SharedUsed)}/{Format.Bytes(dedicatedTotal + sharedTotal)}");
        if (s.TemperatureC is { } temp) Set(LiveStats, "GPU temperature", $"{temp:0} °C");
    }

    /// <summary>Swap in an engine the adapter actually has for any default it lacks (Arc has no "Video Encode").</summary>
    private void ResolveSlots()
    {
        _slotsResolved = true;
        foreach (var slot in Slots)
        {
            if (History.ContainsKey(slot.Name)) continue;
            var replacement = History.Keys.FirstOrDefault(k => Slots.All(o => o.Name != k));
            if (replacement is not null) slot.Selected = replacement;
        }
        foreach (var name in EngineNames.ToList())
            if (!History.ContainsKey(name) && Slots.All(s => s.Name != name))
                EngineNames.Remove(name);
    }

    public override async Task LoadStaticAsync()
    {
        var adapter = _adapter;
        ShowStatic(await Background(() => GpuInfoReader.Load(adapter)));
    }

    private void ShowStatic(GpuInfo? info)
    {
        Replace(StaticInfo,
        [
            ("Manufacturer", info?.Manufacturer ?? GpuInfoReader.VendorName(_adapter.VendorId)),
            ("Model", _adapter.Name),
            ("Driver version", info?.DriverVersion ?? ""),
            ("Driver date", info?.DriverDate ?? ""),
            ("Driver model", info?.Wddm ?? ""),
            ("Physical location", info?.Location ?? ""),
            ("PCIe link (current)", info?.CurrentLink ?? ""),
            ("PCIe link (max)", info?.MaxLink ?? ""),
            ("Device ID", info?.HardwareId ?? ""),
            ("Dedicated memory", Format.Bytes(_adapter.DedicatedVideoMemory)),
            ("Shared memory", Format.Bytes(_adapter.SharedSystemMemory)),
        ]);
    }
}
