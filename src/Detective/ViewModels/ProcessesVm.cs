using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Detective.Core;
using Microsoft.UI.Xaml;

namespace Detective.ViewModels;

public enum ProcessColumn
{
    Name,
    Pid,
    Cpu,
    Memory,
    Disk,
    Network,
    Gpu,
}

/// <summary>Widths of the resizable columns, shared by the header and every row template.</summary>
public sealed partial class ProcessColumns : ObservableObject
{
    public const double MinWidth = 56;

    public static readonly IReadOnlyDictionary<ProcessColumn, double> Defaults = new Dictionary<ProcessColumn, double>
    {
        [ProcessColumn.Pid] = 72,
        [ProcessColumn.Cpu] = 80,
        [ProcessColumn.Memory] = 100,
        [ProcessColumn.Disk] = 96,
        [ProcessColumn.Network] = 96,
        [ProcessColumn.Gpu] = 80,
    };

    // Declared after Defaults: static fields initialize in order, and the constructor reads them.
    public static ProcessColumns Current { get; } = new();

    [ObservableProperty] private GridLength _pid = new(Defaults[ProcessColumn.Pid]);
    [ObservableProperty] private GridLength _cpu = new(Defaults[ProcessColumn.Cpu]);
    [ObservableProperty] private GridLength _memory = new(Defaults[ProcessColumn.Memory]);
    [ObservableProperty] private GridLength _disk = new(Defaults[ProcessColumn.Disk]);
    [ObservableProperty] private GridLength _network = new(Defaults[ProcessColumn.Network]);
    [ObservableProperty] private GridLength _gpu = new(Defaults[ProcessColumn.Gpu]);

    public double Get(ProcessColumn column) => column switch
    {
        ProcessColumn.Pid => Pid.Value,
        ProcessColumn.Cpu => Cpu.Value,
        ProcessColumn.Memory => Memory.Value,
        ProcessColumn.Disk => Disk.Value,
        ProcessColumn.Network => Network.Value,
        ProcessColumn.Gpu => Gpu.Value,
        _ => 0,
    };

    public void Set(ProcessColumn column, double width)
    {
        var length = new GridLength(Math.Round(Math.Clamp(width, MinWidth, 400)));
        switch (column)
        {
            case ProcessColumn.Pid: Pid = length; break;
            case ProcessColumn.Cpu: Cpu = length; break;
            case ProcessColumn.Memory: Memory = length; break;
            case ProcessColumn.Disk: Disk = length; break;
            case ProcessColumn.Network: Network = length; break;
            case ProcessColumn.Gpu: Gpu = length; break;
        }
    }
}

/// <summary>One line of the process table: a group heading ("Apps (5)") or a process.</summary>
public sealed partial class ProcessRowVm : ObservableObject
{
    private ProcessRowVm(string? group, ProcessKey key)
    {
        Group = group;
        Key = key;
    }

    public static ProcessRowVm ForGroup(string group) => new(group, default);

    public static ProcessRowVm ForProcess(ProcessKey key) => new(null, key);

    /// <summary>Set for heading rows: "Apps" or "Background processes".</summary>
    public string? Group { get; }

    public bool IsGroup => Group is not null;

    public ProcessKey Key { get; }

    /// <summary>Latest sample (null for headings).</summary>
    public ProcessSample? Sample { get; set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _pid = "";
    [ObservableProperty] private string _cpu = "";
    [ObservableProperty] private string _memory = "";
    [ObservableProperty] private string _disk = "";
    [ObservableProperty] private string _network = "";
    [ObservableProperty] private string _gpu = "";
    [ObservableProperty] private double _cpuHeat;
    [ObservableProperty] private double _memoryHeat;
    [ObservableProperty] private double _diskHeat;
    [ObservableProperty] private double _networkHeat;
    [ObservableProperty] private double _gpuHeat;
    [ObservableProperty] private Thickness _indent;
    [ObservableProperty] private bool _hasChildren;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _toolTip = "";

    /// <summary>What Narrator and UI Automation read for the row.</summary>
    public override string ToString() => IsGroup ? Name : $"{Name}, PID {Pid}";
}

/// <summary>
/// The Processes page: Apps and Background groups, each a parent/child tree, sortable on any column. A collapsed
/// process shows the totals of its whole tree (as Task Manager does for grouped rows); an expanded one shows its own.
/// </summary>
public sealed partial class ProcessesVm : ObservableObject
{
    private const double IndentStep = 20;
    private const double DiskHeatScale = 50 * 1024 * 1024;   // 50 MB/s reads as fully "hot"
    private const double NetworkHeatScale = 100_000_000;    // 100 Mbps

    private readonly AppSettings _settings;
    private readonly HashSet<ProcessKey> _expanded = new();
    private readonly HashSet<string> _collapsedGroups = new();
    private readonly Dictionary<ProcessKey, ProcessRowVm> _rows = new();

    /// <summary>
    /// Each visible process's position after the last rebuild. Rows keep these positions between ticks so the
    /// list stays still while values change; only a click on a column header re-sorts everything.
    /// </summary>
    private Dictionary<ProcessKey, int> _rank = new();
    private readonly ProcessRowVm _appsHeading = ProcessRowVm.ForGroup("Apps");
    private readonly ProcessRowVm _backgroundHeading = ProcessRowVm.ForGroup("Background processes");
    private ProcessList? _last;
    private double _gpuBusiest;
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    public ProcessesVm(AppSettings settings)
    {
        _settings = settings;
        _sortColumn = Enum.TryParse<ProcessColumn>(settings.ProcessSort, out var c) ? c : ProcessColumn.Cpu;
        _sortDescending = settings.ProcessSortDescending;
        foreach (var (name, width) in settings.ProcessColumnWidths)
            if (Enum.TryParse<ProcessColumn>(name, out var column))
                ProcessColumns.Current.Set(column, width);
    }

    public ProcessRowCollection Rows { get; } = new();

    /// <summary>Raised after <see cref="Rows"/> is reordered, so the view can restore the selection.</summary>
    public event EventHandler? RowsUpdated;

    public static bool IsElevated { get; } = Environment.IsPrivilegedProcess;

    [ObservableProperty] private ProcessColumn _sortColumn;
    [ObservableProperty] private bool _sortDescending;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _networkAvailable = true;
    [ObservableProperty] private ProcessRowVm? _selected;
    [ObservableProperty] private string _cpuTotal = "";
    [ObservableProperty] private string _memoryTotal = "";
    [ObservableProperty] private string _diskTotal = "";
    [ObservableProperty] private string _networkTotal = "";
    [ObservableProperty] private string _gpuTotal = "";
    [ObservableProperty] private string _countText = "";

    /// <summary>The selected row is a process that may be ended.</summary>
    public bool CanEnd => Selected?.Sample is { } s && ProcessKiller.Forbidden(s.Pid) is null;

    partial void OnSelectedChanged(ProcessRowVm? value) => OnPropertyChanged(nameof(CanEnd));

    partial void OnFilterChanged(string value) => Rebuild();

    public void SortBy(ProcessColumn column)
    {
        if (SortColumn == column)
            SortDescending = !SortDescending;
        else
        {
            SortColumn = column;
            // Text sorts A→Z first; measurements show the biggest first.
            SortDescending = column is not (ProcessColumn.Name or ProcessColumn.Pid);
        }
        _settings.ProcessSort = SortColumn.ToString();
        _settings.ProcessSortDescending = SortDescending;
        _settings.Save();
        _rank.Clear();
        Rebuild();
    }

    public void SaveColumnWidths()
    {
        _settings.ProcessColumnWidths = ProcessColumns.Defaults.Keys.ToDictionary(c => c.ToString(), c => ProcessColumns.Current.Get(c));
        _settings.Save();
    }

    public void Toggle(ProcessRowVm row)
    {
        if (row.Group is { } g)
        {
            if (!_collapsedGroups.Remove(g)) _collapsedGroups.Add(g);
        }
        else if (!_expanded.Remove(row.Key))
        {
            _expanded.Add(row.Key);
        }
        Rebuild();
    }

    public void SetExpanded(ProcessRowVm row, bool expanded)
    {
        if (row.IsExpanded != expanded && (row.HasChildren || row.IsGroup)) Toggle(row);
    }

    public void Apply(ProcessList list, IReadOnlyList<GpuSample> gpus, CpuSample? cpu)
    {
        _last = ScaleCpu(list, cpu);
        _gpuBusiest = gpus.Count == 0 ? 0 : gpus.Max(g => g.Utilization);
        NetworkAvailable = list.NetworkAvailable;
        Rebuild();
    }

    /// <summary>
    /// Process CPU is a share of processor time, but the CPU page shows "% Processor Utility", which also counts
    /// turbo frequency. Like Task Manager, scale the processes so they add up to the CPU page's figure.
    /// </summary>
    private static ProcessList ScaleCpu(ProcessList list, CpuSample? cpu)
    {
        double busy = list.Items.Where(s => s.Pid != 0).Sum(s => s.Cpu);
        if (cpu is null || busy < 0.1) return list;
        double factor = Math.Clamp(cpu.Utilization / busy, 0.5, 3);
        return list with { Items = list.Items.Select(s => s with { Cpu = Math.Min(100, s.Cpu * factor) }).ToList() };
    }

    /// <summary>A node's values: its own, or its whole tree's while collapsed.</summary>
    private readonly record struct Totals(double Cpu, double Memory, double Disk, double? Network, double Gpu, int Count)
    {
        public static Totals Of(ProcessSample s) => new(s.Cpu, s.Memory, s.DiskBytesPerSec, s.NetworkBitsPerSec, s.Gpu, 1);

        public static Totals operator +(Totals a, Totals b) =>
            new(a.Cpu + b.Cpu, a.Memory + b.Memory, a.Disk + b.Disk,
                a.Network is null && b.Network is null ? null : (a.Network ?? 0) + (b.Network ?? 0),
                a.Gpu + b.Gpu, a.Count + b.Count);
    }

    /// <summary>A node prepared for display: filtered children and the values its row shows.</summary>
    private sealed class Item(ProcessSample sample, List<Item> children, Totals subtree, bool expanded)
    {
        public ProcessSample Sample { get; } = sample;
        public List<Item> Children { get; } = children;
        public Totals Subtree { get; } = subtree;
        public bool Expanded { get; } = expanded;
        public Totals Shown => Expanded || Children.Count == 0 ? Totals.Of(Sample) : Subtree;
    }

    private void Rebuild()
    {
        if (_last is not { } list) return;

        // The System Idle Process is idle time, not a process; Task Manager leaves it out of this page too.
        var samples = list.Items.Where(s => s.Pid != 0).ToList();
        var (apps, background) = ProcessTree.Group(samples);
        string filter = Filter.Trim();

        var target = new List<ProcessRowVm>(samples.Count + 2);
        int appCount = AddGroup(_appsHeading, apps, filter, target, list);
        int backgroundCount = AddGroup(_backgroundHeading, background, filter, target, list);

        CountText = filter.Length == 0
            ? $"{samples.Count.ToString("N0", Culture)} processes"
            : $"{(appCount + backgroundCount).ToString("N0", Culture)} of {samples.Count.ToString("N0", Culture)} processes";
        CpuTotal = Format.Percent(Math.Min(100, samples.Sum(s => s.Cpu)));
        MemoryTotal = Format.Percent(list.MemoryLoad);
        DiskTotal = MegabytesPerSecond(samples.Sum(s => s.DiskBytesPerSec));
        NetworkTotal = list.NetworkAvailable ? Megabits(samples.Sum(s => s.NetworkBitsPerSec ?? 0)) : "—";
        GpuTotal = Format.Percent(_gpuBusiest);

        Reconcile(target);
        _rank = new Dictionary<ProcessKey, int>(target.Count);
        for (int i = 0; i < target.Count; i++)
            if (!target[i].IsGroup) _rank[target[i].Key] = i;
        var live = samples.Select(s => s.Key).ToHashSet();
        foreach (var gone in _rows.Keys.Where(k => !live.Contains(k)).ToList()) _rows.Remove(gone);
        _expanded.IntersectWith(live);
        if (Selected is { IsGroup: false } sel && !live.Contains(sel.Key)) Selected = null;
        RowsUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a group's heading and visible rows; returns how many processes matched.</summary>
    private int AddGroup(ProcessRowVm heading, List<ProcessNode> roots, string filter, List<ProcessRowVm> target, ProcessList list)
    {
        var items = Prepare(roots, filter);
        if (items.Count == 0) return 0;

        int matched = items.Sum(i => i.Subtree.Count);
        bool collapsed = _collapsedGroups.Contains(heading.Group!) && filter.Length == 0;
        // Apps count apps (as Task Manager does); background counts processes.
        int shown = heading == _appsHeading ? items.Count : matched;
        heading.Name = $"{heading.Group} ({shown.ToString("N0", Culture)})";
        heading.HasChildren = true;
        heading.IsExpanded = !collapsed;
        heading.Indent = new Thickness(0, 0, 0, 0);
        target.Add(heading);
        if (!collapsed) Flatten(items, 0, target, list);
        return matched;
    }

    private List<Item> Prepare(List<ProcessNode> nodes, string filter)
    {
        var items = new List<Item>(nodes.Count);
        foreach (var node in nodes)
        {
            // A match keeps its whole tree, so a search shows what "End process tree" would end.
            bool matches = filter.Length == 0 || Matches(node.Sample, filter);
            var children = Prepare(node.Children, matches ? "" : filter);
            if (!matches && children.Count == 0) continue;
            var subtree = children.Aggregate(Totals.Of(node.Sample), (t, c) => t + c.Subtree);
            // While searching, every ancestor of a match opens so the match is visible.
            bool expanded = children.Count > 0 && ((filter.Length > 0 && !matches) || _expanded.Contains(node.Sample.Key));
            items.Add(new Item(node.Sample, children, subtree, expanded));
        }
        Order(items);
        return items;
    }

    /// <summary>
    /// Siblings that were already on screen keep their order. New ones (just started, or just revealed by
    /// expanding a row) go where the current sort would put them. With no history yet, a plain sort.
    /// </summary>
    private void Order(List<Item> items)
    {
        if (_rank.Count == 0)
        {
            items.Sort(Compare);
            return;
        }
        var ordered = items.Where(i => _rank.ContainsKey(i.Sample.Key)).OrderBy(i => _rank[i.Sample.Key]).ToList();
        var fresh = items.Where(i => !_rank.ContainsKey(i.Sample.Key)).ToList();
        fresh.Sort(Compare);
        foreach (var item in fresh)
        {
            int at = ordered.FindIndex(o => Compare(item, o) < 0);
            if (at < 0) ordered.Add(item);
            else ordered.Insert(at, item);
        }
        items.Clear();
        items.AddRange(ordered);
    }

    private static bool Matches(ProcessSample s, string filter) =>
        s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (s.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || s.Pid.ToString(CultureInfo.InvariantCulture).StartsWith(filter, StringComparison.Ordinal);

    private int Compare(Item a, Item b)
    {
        Totals x = a.Shown, y = b.Shown;
        int c = SortColumn switch
        {
            ProcessColumn.Name => string.Compare(a.Sample.DisplayName, b.Sample.DisplayName, StringComparison.CurrentCultureIgnoreCase),
            ProcessColumn.Pid => a.Sample.Pid.CompareTo(b.Sample.Pid),
            ProcessColumn.Cpu => x.Cpu.CompareTo(y.Cpu),
            ProcessColumn.Memory => x.Memory.CompareTo(y.Memory),
            ProcessColumn.Disk => x.Disk.CompareTo(y.Disk),
            ProcessColumn.Network => (x.Network ?? 0).CompareTo(y.Network ?? 0),
            ProcessColumn.Gpu => x.Gpu.CompareTo(y.Gpu),
            _ => 0,
        };
        if (SortDescending) c = -c;
        // Stable tie-break so equal rows don't swap places from tick to tick.
        if (c == 0) c = string.Compare(a.Sample.DisplayName, b.Sample.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        return c != 0 ? c : a.Sample.Pid.CompareTo(b.Sample.Pid);
    }

    private void Flatten(List<Item> items, int depth, List<ProcessRowVm> target, ProcessList list)
    {
        foreach (var item in items)
        {
            var s = item.Sample;
            if (!_rows.TryGetValue(s.Key, out var row)) _rows[s.Key] = row = ProcessRowVm.ForProcess(s.Key);
            row.Sample = s;
            var shown = item.Shown;
            bool rolledUp = !item.Expanded && item.Children.Count > 0;

            row.Name = rolledUp ? $"{s.DisplayName} ({shown.Count.ToString("N0", Culture)})" : s.DisplayName;
            row.Pid = s.Pid.ToString(Culture);
            row.Cpu = shown.Cpu.ToString("0.0", Culture) + "%";
            row.Memory = (shown.Memory / (1024 * 1024)).ToString("0.0", Culture) + " MB";
            row.Disk = MegabytesPerSecond(shown.Disk);
            row.Network = shown.Network is { } n ? Megabits(n) : "";
            row.Gpu = Math.Min(100, shown.Gpu).ToString("0.0", Culture) + "%";
            row.CpuHeat = shown.Cpu / 100;
            row.MemoryHeat = list.TotalPhysicalMemory == 0 ? 0 : shown.Memory / list.TotalPhysicalMemory;
            row.DiskHeat = shown.Disk / DiskHeatScale;
            row.NetworkHeat = (shown.Network ?? 0) / NetworkHeatScale;
            row.GpuHeat = shown.Gpu / 100;
            row.Indent = new Thickness(depth * IndentStep, 0, 0, 0);
            row.HasChildren = item.Children.Count > 0;
            row.IsExpanded = item.Expanded;
            row.ToolTip = s.Path is { } path ? $"{s.Name}\n{path}" : s.Name;
            target.Add(row);

            if (item.Expanded) Flatten(item.Children, depth + 1, target, list);
        }
    }

    private static string MegabytesPerSecond(double bytesPerSec) =>
        (bytesPerSec / (1024 * 1024)).ToString("0.0", Culture) + " MB/s";

    private static string Megabits(double bitsPerSec) =>
        (bitsPerSec / 1_000_000).ToString("0.0", Culture) + " Mbps";

    /// <summary>Brings <see cref="Rows"/> to the target order with moves rather than a reset, so the list doesn't flash.</summary>
    private void Reconcile(List<ProcessRowVm> target)
    {
        var keep = new HashSet<ProcessRowVm>(target);
        for (int i = Rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(Rows[i])) Rows.RemoveAt(i);

        for (int i = 0; i < target.Count; i++)
        {
            var row = target[i];
            if (i < Rows.Count && ReferenceEquals(Rows[i], row)) continue;
            if (Rows.IndexOf(row) is int j and >= 0)
                Rows.Move(j, i);
            else
                Rows.Insert(i, row);
        }
    }
}
