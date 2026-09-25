using CommunityToolkit.Mvvm.ComponentModel;
using Detective.Core;
using Detective.Core.Native;

namespace Detective.ViewModels;

/// <summary>One connected physical network adapter.</summary>
public sealed partial class NetworkVm : FocusItemVm
{
    private const int AddressRefreshTicks = 10;

    private NetworkSample? _last;
    private NetworkAdapterInfo? _info;
    private NetworkAddresses? _addresses;
    private int _ticks;

    public NetworkVm(NetworkSample first) : base(FocusKind.Network, first.Id, Accents.Network)
    {
        TileSecondary = new RingBuffer();
        Title = first.TypeLabel;
        Subtitle = first.Description;
    }

    /// <summary>Receive is the filled series, send the dashed one (Task Manager's convention).</summary>
    public RingBuffer Receive => TileSeries;

    public RingBuffer Send => TileSecondary!;

    [ObservableProperty]
    private string _throughputMaxLabel = "";

    /// <summary>Tile label, e.g. "Wi-Fi" or "Ethernet 2" when there are several of a type.</summary>
    public string TypeLabel { get; set; } = "";

    public void Apply(NetworkSample s)
    {
        bool wlanChanged = _last?.Wlan != s.Wlan;
        _last = s;
        Receive.Add((float)s.ReceiveBitsPerSec);
        Send.Add((float)s.SendBitsPerSec);

        TileMaximum = NiceScale.Decimal(Math.Max(Receive.Max(), Send.Max()), 100_000);
        ThroughputMaxLabel = Format.BitRate(TileMaximum);

        Title = TypeLabel;
        TileTitle = TypeLabel;
        TileSubtitle = $"S: {Format.BitRate(s.SendBitsPerSec)}  R: {Format.BitRate(s.ReceiveBitsPerSec)}";

        Set(LiveStats, "Send", Format.BitRate(s.SendBitsPerSec));
        Set(LiveStats, "Receive", Format.BitRate(s.ReceiveBitsPerSec));
        if (s.Wlan is { AccessDenied: false } w) Set(LiveStats, "Signal strength", $"{w.SignalQuality}%");

        if (++_ticks % AddressRefreshTicks == 0) _ = RefreshAddressesAsync();
        else if (wlanChanged) ShowStatic();
    }

    public override async Task LoadStaticAsync()
    {
        var id = Key;
        _info = await Background(() => NetworkInfoReader.Load(id));
        await RefreshAddressesAsync();
    }

    private async Task RefreshAddressesAsync()
    {
        var id = Key;
        _addresses = await Background(() => NetworkInfoReader.LoadAddresses(id)) ?? _addresses;
        ShowStatic();
    }

    private void ShowStatic()
    {
        var s = _last;
        var w = s?.Wlan;
        string ssid = w is null ? "" : w.AccessDenied
            ? "Hidden: turn on Location in Settings > Privacy & security > Location"
            : w.Ssid ?? "";

        var rows = new List<(string, string)>
        {
            ("Adapter name", s?.Name ?? ""),
            ("Manufacturer", _info?.Manufacturer ?? ""),
            ("Model", s?.Description ?? ""),
            ("Connection type", TypeLabel),
            ("Link speed", s is { LinkSpeed: > 0 } ? Format.BitRate(s.LinkSpeed) : ""),
            ("SSID", ssid),
            ("Wi-Fi standard", w is { AccessDenied: false } ? w.PhyType : ""),
            ("Channel", w is { Channel: > 0 } ? w.Channel.ToString() : ""),
            ("IPv4 address", Join(_addresses?.IPv4)),
            ("IPv6 address", Join(_addresses?.IPv6)),
            ("Default gateway", Join(_addresses?.Gateways)),
            ("DNS servers", Join(_addresses?.Dns)),
            ("DNS suffix", _addresses?.DnsSuffix ?? ""),
            ("MAC address", _info?.Mac ?? ""),
            ("Driver", _info is null ? "" : $"{_info.DriverProvider} {_info.DriverVersion}".Trim()),
        };
        Replace(StaticInfo, rows);
    }

    private static string Join(IReadOnlyList<string>? values) => values is null ? "" : string.Join("\n", values);
}
