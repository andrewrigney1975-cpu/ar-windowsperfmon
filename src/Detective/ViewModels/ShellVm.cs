using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Detective.Controls;
using Detective.Core;

namespace Detective.ViewModels;

/// <summary>
/// Owns the ordered list of focus items (the summary strip) and the single selected item that
/// both the left rail and the strip reflect.
/// </summary>
public sealed partial class ShellVm : ObservableObject
{
    private readonly Dictionary<FocusKind, FocusItemVm> _lastByKind = new();
    private readonly AppSettings _settings;

    /// <summary>
    /// False until the saved focus has been restored (after the first snapshot, when disks, networks and
    /// GPUs exist), so the startup default of CPU doesn't overwrite the saved choice.
    /// </summary>
    private bool _focusRestored;

    public ShellVm(AppSettings settings)
    {
        _settings = settings;
        Items.Add(Cpu);
        Items.Add(Memory);
        _ = Cpu.LoadStaticAsync();
        _ = Memory.LoadStaticAsync();
        Selected = Cpu;

        Cpu.ShowLogical = settings.CpuShowLogical;
        Cpu.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(CpuVm.ShowLogical) || _settings.CpuShowLogical == Cpu.ShowLogical) return;
            _settings.CpuShowLogical = Cpu.ShowLogical;
            _settings.Save();
        };
    }

    public CpuVm Cpu { get; } = new();

    public MemoryVm Memory { get; } = new();

    public ObservableCollection<FocusItemVm> Items { get; } = new();

    [ObservableProperty]
    private FocusItemVm? _selected;

    partial void OnSelectedChanged(FocusItemVm? oldValue, FocusItemVm? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            _lastByKind[newValue.Kind] = newValue;
        }
        if (_focusRestored && SavedSelection != _settings.LastFocus)
        {
            _settings.LastFocus = SavedSelection;
            _settings.Save();
        }
    }

    /// <summary>Rail click: the most recently viewed item of that kind, else the first one.</summary>
    public void SelectKind(FocusKind kind)
    {
        if (_lastByKind.TryGetValue(kind, out var last) && Items.Contains(last))
            Selected = last;
        else if (Items.FirstOrDefault(i => i.Kind == kind) is { } first)
            Selected = first;
    }

    /// <summary>Restores the selection saved as "Kind:PersistKey"; if that item is gone, the first of its kind.</summary>
    private void RestoreFocus()
    {
        var saved = _settings.LastFocus;
        if (saved is not null)
        {
            var match = Items.FirstOrDefault(i => $"{i.Kind}:{i.PersistKey}" == saved);
            if (match is null && Enum.TryParse<FocusKind>(saved.Split(':')[0], out var kind))
                match = Items.FirstOrDefault(i => i.Kind == kind);
            if (match is not null) Selected = match;
        }
        _focusRestored = true;
    }

    public string? SavedSelection => Selected is { } s ? $"{s.Kind}:{s.PersistKey}" : null;

    public void Apply(Snapshot snapshot)
    {
        if (snapshot.Cpu is { } cpu) Cpu.Apply(cpu);
        if (snapshot.Memory is { } memory) Memory.Apply(memory);

        Reconcile(FocusKind.Disk, snapshot.Disks, d => $"disk:{d.Number}", d => new DiskVm(d.Number), (vm, d) => vm.Apply(d));
        LabelNetworks(snapshot.Networks);
        Reconcile(FocusKind.Network, snapshot.Networks, n => n.Id, n => new NetworkVm(n), (vm, n) => vm.Apply(n));
        Reconcile(FocusKind.Gpu, snapshot.Gpus, g => g.Adapter.LuidKey, CreateGpu, (vm, g) => vm.Apply(g));

        if (!_focusRestored) RestoreFocus();
        ChartClock.Advance();
    }

    private GpuVm CreateGpu(GpuSample sample)
    {
        var vm = new GpuVm(sample.Adapter, _settings.GpuEngines.GetValueOrDefault($"{sample.Adapter.Index}:{sample.Adapter.Name}"));
        vm.EnginesChanged += (_, _) =>
        {
            _settings.GpuEngines[vm.PersistKey] = vm.SelectedEngines;
            _settings.Save();
        };
        return vm;
    }

    private readonly Dictionary<string, string> _networkLabels = new();

    /// <summary>Tile label is the network type; duplicates get a number, as Task Manager does ("Ethernet 2").</summary>
    private void LabelNetworks(IReadOnlyList<NetworkSample> networks)
    {
        _networkLabels.Clear();
        foreach (var group in networks.GroupBy(n => n.TypeLabel))
        {
            int i = 0;
            foreach (var n in group)
                _networkLabels[n.Id] = i++ == 0 ? group.Key : $"{group.Key} {i}";
        }
        foreach (var vm in Items.OfType<NetworkVm>())
            if (_networkLabels.TryGetValue(vm.Key, out var label))
                vm.TypeLabel = label;
    }

    private void Reconcile<TVm, TSample>(
        FocusKind kind,
        IReadOnlyList<TSample> samples,
        Func<TSample, string> key,
        Func<TSample, TVm> create,
        Action<TVm, TSample> apply)
        where TVm : FocusItemVm
    {
        var existing = Items.OfType<TVm>().Where(i => i.Kind == kind).ToDictionary(i => i.Key);
        var seen = new HashSet<string>();

        foreach (var sample in samples)
        {
            string k = key(sample);
            if (!seen.Add(k)) continue;
            if (!existing.TryGetValue(k, out var vm))
            {
                vm = create(sample);
                if (vm is NetworkVm n && _networkLabels.TryGetValue(k, out var label)) n.TypeLabel = label;
                // Insert at the end of this kind's block to keep the strip grouped CPU → Memory → Disks → Networks → GPUs.
                int index = Items.Count(i => i.Kind <= kind);
                Items.Insert(index, vm);
                _ = vm.LoadStaticAsync();
            }
            apply(vm, sample);
        }

        foreach (var (k, vm) in existing)
        {
            if (seen.Contains(k)) continue;
            Items.Remove(vm);
            if (Selected == vm)
            {
                _lastByKind.Remove(kind);
                Selected = Items.FirstOrDefault(i => i.Kind == kind) ?? Cpu;
            }
        }
    }
}
