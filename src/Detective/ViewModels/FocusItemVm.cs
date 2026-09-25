using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Detective.Core;
using Microsoft.UI;
using Windows.UI;

namespace Detective.ViewModels;

/// <summary>Order matters: the summary strip lists items in this order.</summary>
public enum FocusKind
{
    Cpu,
    Memory,
    Disk,
    Network,
    Gpu,
}

/// <summary>Task Manager's per-resource accent colours.</summary>
public static class Accents
{
    public static readonly Color Cpu = ColorHelper.FromArgb(255, 0x11, 0x7D, 0xBB);
    public static readonly Color Memory = ColorHelper.FromArgb(255, 0x8B, 0x12, 0xAE);
    public static readonly Color Disk = ColorHelper.FromArgb(255, 0x4D, 0xA6, 0x0C);
    public static readonly Color Network = ColorHelper.FromArgb(255, 0xA7, 0x4F, 0x01);
    public static readonly Color Gpu = ColorHelper.FromArgb(255, 0x0B, 0x85, 0x79);
}

/// <summary>A label/value pair shown in a details block.</summary>
public sealed partial class Stat : ObservableObject
{
    public Stat(string label, string value)
    {
        Label = label;
        _value = value;
    }

    public string Label { get; }

    [ObservableProperty]
    private string _value;
}

/// <summary>Shared chart-window state: the x-axis caption and the summary tile width.</summary>
public sealed partial class ChartWindow : ObservableObject
{
    public static ChartWindow Current { get; } = new();

    [ObservableProperty]
    private string _caption = "60 seconds";

    [ObservableProperty]
    private double _tileWidth = 200;

    [ObservableProperty]
    private double _tileHeight = 120;

    /// <summary>Details blocks scroll beyond this so charts keep most of the focus area.</summary>
    [ObservableProperty]
    private double _detailsMaxHeight = 260;

    public void SetInterval(TimeSpan interval)
    {
        double seconds = interval.TotalSeconds * RingBuffer.DefaultCapacity;
        Caption = seconds < 120 ? $"{seconds:0} seconds" : $"{seconds / 60:0} minutes";
    }
}

/// <summary>
/// Anything that can be the focus of the upper section and has a tile in the summary strip.
/// All members are touched on the UI thread only.
/// </summary>
public abstract partial class FocusItemVm : ObservableObject
{
    protected FocusItemVm(FocusKind kind, string key, Color accent)
    {
        Kind = kind;
        Key = key;
        Accent = accent;
    }

    public FocusKind Kind { get; }

    /// <summary>Stable identity used to reconcile items across snapshots.</summary>
    public string Key { get; }

    /// <summary>Identity that survives a reboot, used to remember the last opened item.</summary>
    public virtual string PersistKey => Key;

    public Color Accent { get; }

    /// <summary>Series shown on the summary tile; views may reuse it for their main chart.</summary>
    public RingBuffer TileSeries { get; } = new();

    public RingBuffer? TileSecondary { get; protected init; }

    public ObservableCollection<Stat> LiveStats { get; } = new();

    public ObservableCollection<Stat> StaticInfo { get; } = new();

    [ObservableProperty]
    private double _tileMaximum = 100;

    [ObservableProperty]
    private string _tileTitle = "";

    [ObservableProperty]
    private string _tileSubtitle = "";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _subtitle = "";

    /// <summary>Loads slow, static hardware details (WMI, SetupAPI) off the UI thread.</summary>
    public virtual Task LoadStaticAsync() => Task.CompletedTask;

    /// <summary>Adds or updates a stat in place so bound TextBlocks update without re-templating.</summary>
    protected static void Set(ObservableCollection<Stat> list, string label, string value)
    {
        foreach (var stat in list)
        {
            if (stat.Label == label)
            {
                stat.Value = value;
                return;
            }
        }
        list.Add(new Stat(label, value));
    }

    /// <summary>Replaces a static block in one go (it changes rarely).</summary>
    protected static void Replace(ObservableCollection<Stat> list, IEnumerable<(string Label, string Value)> rows)
    {
        list.Clear();
        foreach (var (label, value) in rows)
            if (!string.IsNullOrWhiteSpace(value))
                list.Add(new Stat(label, value));
    }

    protected static async Task<T?> Background<T>(Func<T?> work) where T : class
    {
        try { return await Task.Run(work); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
    }
}
