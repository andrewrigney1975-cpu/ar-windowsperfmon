using CommunityToolkit.Mvvm.ComponentModel;
using Detective.Core;

namespace Detective.ViewModels;

/// <summary>One physical disk; its volumes roll up into it.</summary>
public sealed partial class DiskVm : FocusItemVm
{
    private const int VolumeRefreshTicks = 30;

    private DiskInfo? _info;
    private List<VolumeInfo> _volumes = new();
    private string _letters = "";
    private int _ticks;
    private bool _volumeRefreshPending;

    public DiskVm(int number) : base(FocusKind.Disk, $"disk:{number}", Accents.Disk)
    {
        Number = number;
        TileSecondary = new RingBuffer();
        Title = $"Disk {number}";
    }

    public int Number { get; }

    /// <summary>Read rate is the tile's primary series, write rate its dashed secondary.</summary>
    public RingBuffer Read => TileSeries;

    public RingBuffer Write => TileSecondary!;

    public RingBuffer Active { get; } = new();

    [ObservableProperty]
    private string _transferMaxLabel = "";

    public void Apply(DiskSample s)
    {
        Active.Add((float)s.ActivePercent);
        Read.Add((float)s.ReadBytesPerSec);
        Write.Add((float)s.WriteBytesPerSec);

        TileMaximum = NiceScale.Binary(Math.Max(Read.Max(), Write.Max()), 1024 * 1024);
        TransferMaxLabel = Format.ByteRate(TileMaximum);

        if (s.Letters != _letters)
        {
            _letters = s.Letters;
            Title = _letters == "" ? $"Disk {Number}" : $"Disk {Number} ({_letters})";
            _ = RefreshVolumesAsync();
        }
        else if (++_ticks % VolumeRefreshTicks == 0)
        {
            _ = RefreshVolumesAsync();
        }

        TileTitle = _letters == "" ? $"Disk {Number}" : _letters;
        TileSubtitle = $"{Format.Percent(s.ActivePercent)}  R {Format.ByteRate(s.ReadBytesPerSec)}  W {Format.ByteRate(s.WriteBytesPerSec)}";

        Set(LiveStats, "Active time", Format.Percent(s.ActivePercent));
        Set(LiveStats, "Average response time", $"{s.AvgResponseMs:0.0} ms");
        Set(LiveStats, "Read speed", Format.ByteRate(s.ReadBytesPerSec));
        Set(LiveStats, "Write speed", Format.ByteRate(s.WriteBytesPerSec));
    }

    public override async Task LoadStaticAsync()
    {
        _info = await Background(() => DiskInfoReader.Load(Number));
        if (_info is not null) Subtitle = _info.Model;
        ShowStatic();
    }

    private async Task RefreshVolumesAsync()
    {
        if (_volumeRefreshPending) return;
        _volumeRefreshPending = true;
        try
        {
            var letters = _letters;
            _volumes = await Background(() => DiskInfoReader.LoadVolumes(letters)) ?? _volumes;
            ShowStatic();
        }
        finally
        {
            _volumeRefreshPending = false;
        }
    }

    private void ShowStatic()
    {
        var info = _info;
        ulong formatted = (ulong)_volumes.Sum(v => (long)v.Capacity);
        string type = info is null ? "" : info.MediaType == "" ? info.Interface : $"{info.MediaType} ({info.Interface})";

        var rows = new List<(string, string)>
        {
            ("Manufacturer", info?.Manufacturer ?? ""),
            ("Model", info?.Model ?? ""),
            ("Type", type),
            ("Capacity", info is { Size: > 0 } ? Format.Bytes(info.Size) : formatted > 0 ? $"{Format.Bytes(formatted)} (from volumes)" : ""),
            ("Formatted", formatted > 0 ? Format.Bytes(formatted) : ""),
            ("System disk", info is null ? "" : info.IsSystem || _volumes.Any(v => v.IsSystem) ? "Yes" : "No"),
            ("Page file", _volumes.Count == 0 ? "" : _volumes.Any(v => v.HasPageFile) ? "Yes" : "No"),
            ("Firmware", info?.Firmware ?? ""),
        };
        foreach (var v in _volumes)
        {
            string label = v.Label == "" ? "" : $" {v.Label}";
            rows.Add(($"Volume {v.Letter}", $"{v.FileSystem} · {Format.Bytes(v.Capacity)} · {Format.Bytes(v.Free)} free{(label == "" ? "" : " ·" + label)}"));
        }
        Replace(StaticInfo, rows);
    }
}
