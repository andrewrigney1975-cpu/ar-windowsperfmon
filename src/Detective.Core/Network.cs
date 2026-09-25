using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Detective.Core.Native;

namespace Detective.Core;

public sealed record NetworkSample(
    string Id,
    string Name,
    string Description,
    string TypeLabel,
    double SendBitsPerSec,
    double ReceiveBitsPerSec,
    long LinkSpeed,
    WlanStatus? Wlan);

public sealed record NetworkAdapterInfo(
    string Manufacturer,
    string DriverProvider,
    string DriverVersion,
    string DriverDate,
    string Mac);

public sealed record NetworkAddresses(
    IReadOnlyList<string> IPv4,
    IReadOnlyList<string> IPv6,
    IReadOnlyList<string> Dns,
    IReadOnlyList<string> Gateways,
    string DnsSuffix);

/// <summary>Per-adapter throughput for connected physical adapters.</summary>
public sealed class NetworkMonitor : IDisposable
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PhysicalQueryThrottle = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WlanInterval = TimeSpan.FromSeconds(5);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, (long Rx, long Tx, TimeSpan At)> _previous = new();
    private readonly Dictionary<string, WlanStatus?> _wlanCache = new();
    private readonly WlanClient _wlan = new();
    private NetworkInterface[] _adapters = [];
    private Dictionary<string, bool>? _hardware;
    private TimeSpan _lastScan = -TimeSpan.FromHours(1);
    private TimeSpan _lastPhysicalQuery = -TimeSpan.FromHours(1);
    private TimeSpan _lastWlan = -TimeSpan.FromHours(1);
    private volatile bool _dirty = true;

    public NetworkMonitor()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _dirty = true;

    public IReadOnlyList<NetworkSample> Sample()
    {
        var now = _clock.Elapsed;
        if (_dirty || now - _lastScan > RescanInterval) Rescan(now);

        bool refreshWlan = now - _lastWlan > WlanInterval;
        if (refreshWlan) _lastWlan = now;

        var samples = new List<NetworkSample>(_adapters.Length);
        foreach (var nic in _adapters)
        {
            long rx, tx;
            try
            {
                var stats = nic.GetIPStatistics();
                rx = stats.BytesReceived;
                tx = stats.BytesSent;
            }
            catch (NetworkInformationException) { continue; }

            double rxRate = 0, txRate = 0;
            if (_previous.TryGetValue(nic.Id, out var prev))
            {
                double seconds = (now - prev.At).TotalSeconds;
                if (seconds > 0)
                {
                    rxRate = Math.Max(0, rx - prev.Rx) * 8 / seconds;
                    txRate = Math.Max(0, tx - prev.Tx) * 8 / seconds;
                }
            }
            _previous[nic.Id] = (rx, tx, now);

            WlanStatus? wlan = null;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            {
                if (refreshWlan || !_wlanCache.ContainsKey(nic.Id))
                    _wlanCache[nic.Id] = Guid.TryParse(nic.Id, out var g) ? _wlan.Query(g) : null;
                wlan = _wlanCache[nic.Id];
            }

            long speed = 0;
            try { speed = nic.Speed; } catch (NetworkInformationException) { }

            samples.Add(new NetworkSample(nic.Id, nic.Name, nic.Description, TypeLabel(nic), txRate, rxRate, speed, wlan));
        }
        return samples;
    }

    private void Rescan(TimeSpan now)
    {
        _dirty = false;
        _lastScan = now;

        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return; }

        var candidates = all.Where(n =>
            n.OperationalStatus == OperationalStatus.Up &&
            n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)).ToArray();

        // Physical-only: MSFT_NetAdapter.HardwareInterface. Re-query when an adapter we have not classified shows up.
        if (_hardware is null || (candidates.Any(n => !_hardware.ContainsKey(n.Id)) && now - _lastPhysicalQuery > PhysicalQueryThrottle))
        {
            _lastPhysicalQuery = now;
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in Wmi.Query(Wmi.StandardCimv2, "SELECT InterfaceGuid, HardwareInterface FROM MSFT_NetAdapter"))
                map[o.Str("InterfaceGuid")] = o.Bool("HardwareInterface");
            if (map.Count > 0 || _hardware is null) _hardware = map;
        }

        var hardware = _hardware;
        _adapters = candidates.Where(n => hardware.Count == 0 ? LooksPhysical(n) : hardware.GetValueOrDefault(n.Id)).ToArray();

        var live = _adapters.Select(a => a.Id).ToHashSet();
        foreach (var gone in _previous.Keys.Where(k => !live.Contains(k)).ToList()) _previous.Remove(gone);
    }

    /// <summary>Fallback when WMI is unavailable.</summary>
    private static bool LooksPhysical(NetworkInterface n)
    {
        string d = n.Description;
        string[] virtualHints = ["Virtual", "Hyper-V", "VPN", "TAP", "WSL", "Loopback", "Pseudo", "Wintun", "WireGuard", "VMware", "VirtualBox"];
        return n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
               && !virtualHints.Any(h => d.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    internal static string TypeLabel(NetworkInterface n)
    {
        if (n.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) return "Bluetooth";
        return n.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Cellular",
            var t => t.ToString(),
        };
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _wlan.Dispose();
    }
}

public static class NetworkInfoReader
{
    public static NetworkAdapterInfo Load(string interfaceId)
    {
        string manufacturer = "", provider = "", version = "", date = "", mac = "";
        var id = interfaceId.Replace("'", "");

        if (Wmi.Query(Wmi.StandardCimv2,
                $"SELECT DriverProvider, DriverVersionString, DriverDate, PermanentAddress FROM MSFT_NetAdapter WHERE InterfaceGuid = '{id}'")
            .FirstOrDefault() is { } na)
        {
            provider = na.Str("DriverProvider");
            version = na.Str("DriverVersionString");
            date = na.Str("DriverDate");
            mac = FormatMac(na.Str("PermanentAddress"));
        }
        if (Wmi.Query(Wmi.Cimv2, $"SELECT Manufacturer, MACAddress FROM Win32_NetworkAdapter WHERE GUID = '{id}'").FirstOrDefault() is { } wa)
        {
            manufacturer = wa.Str("Manufacturer");
            if (mac == "") mac = wa.Str("MACAddress").Replace(':', '-');
        }
        return new NetworkAdapterInfo(manufacturer, provider, version, date, mac);
    }

    public static NetworkAddresses? LoadAddresses(string interfaceId)
    {
        NetworkInterface? nic;
        try { nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == interfaceId); }
        catch (NetworkInformationException) { return null; }
        if (nic is null) return null;

        var p = nic.GetIPProperties();
        var v4 = p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString()).ToList();
        // Stable addresses first, link-local last; temporary privacy addresses are left out like Task Manager does.
        var v6 = p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6
                                               && a.SuffixOrigin != SuffixOrigin.Random)
            .OrderBy(a => a.Address.IsIPv6LinkLocal)
            .Select(a => a.Address.IsIPv6LinkLocal ? $"{a.Address} (link-local)" : a.Address.ToString()).ToList();
        var dns = p.DnsAddresses.Select(a => a.ToString()).Distinct().ToList();
        var gw = p.GatewayAddresses.Select(g => g.Address.ToString()).Where(s => s is not "0.0.0.0" and not "::").ToList();
        return new NetworkAddresses(v4, v6, dns, gw, p.DnsSuffix);
    }

    internal static string FormatMac(string raw)
    {
        raw = raw.Replace("-", "").Replace(":", "");
        if (raw.Length != 12) return raw;
        return string.Join('-', Enumerable.Range(0, 6).Select(i => raw.Substring(i * 2, 2)));
    }
}
