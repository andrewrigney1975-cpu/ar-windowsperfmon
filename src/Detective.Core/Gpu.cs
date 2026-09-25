using System.Globalization;
using System.Text.RegularExpressions;
using Detective.Core.Native;

namespace Detective.Core;

public sealed record GpuAdapter(
    int Index,
    string Name,
    uint VendorId,
    uint DeviceId,
    uint SubSysId,
    uint Revision,
    uint LuidLow,
    int LuidHigh,
    string LuidKey,
    ulong DedicatedVideoMemory,
    ulong SharedSystemMemory);

public sealed record GpuSample(
    GpuAdapter Adapter,
    IReadOnlyDictionary<string, double> Engines,
    double Utilization,
    ulong DedicatedUsed,
    ulong SharedUsed,
    double? TemperatureC);

public sealed record GpuInfo(
    string Manufacturer,
    string DriverVersion,
    string DriverDate,
    string Wddm,
    string Location,
    string CurrentLink,
    string MaxLink,
    string HardwareId);

public sealed class GpuMonitor : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhCounter? _engine, _dedicated, _shared;
    private readonly List<PdhItem> _items = new();
    private readonly List<(GpuAdapter Adapter, uint Kmt)> _adapters;
    private readonly HashSet<int> _noTemperature = new();

    public GpuMonitor()
    {
        _adapters = Dxgi.EnumerateAdapters().Select(a => (a, D3dkmt.Open(a.LuidLow, a.LuidHigh))).ToList();
        _engine = _query.TryAdd(@"\GPU Engine(*)\Utilization Percentage");
        _dedicated = _query.TryAdd(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _shared = _query.TryAdd(@"\GPU Adapter Memory(*)\Shared Usage");
        _query.Collect();
    }

    public IReadOnlyList<GpuSample> Sample()
    {
        if (_adapters.Count == 0) return [];
        _query.Collect();

        // Sum per physical engine across processes, then take the busiest engine of each type (Task Manager's rule).
        var perEngine = new Dictionary<(string Luid, int Phys, int Eng), (string Type, double Sum)>();
        _items.Clear();
        _engine?.ReadArray(_items);
        foreach (var item in _items)
        {
            if (!GpuInstanceName.TryParseEngine(item.Instance, out var luid, out int phys, out int eng, out var type)) continue;
            var key = (luid, phys, eng);
            perEngine[key] = perEngine.TryGetValue(key, out var e) ? (e.Type, e.Sum + item.Value) : (type, item.Value);
        }

        var dedicated = ReadMemory(_dedicated);
        var shared = ReadMemory(_shared);

        var samples = new List<GpuSample>(_adapters.Count);
        foreach (var (adapter, kmt) in _adapters)
        {
            // Unnamed engines count toward overall utilization but are not offered as chartable functions.
            var engines = new Dictionary<string, double>();
            double unnamed = 0;
            foreach (var ((luid, _, _), (type, sum)) in perEngine)
            {
                if (!luid.Equals(adapter.LuidKey, StringComparison.OrdinalIgnoreCase)) continue;
                double value = Math.Clamp(sum, 0, 100);
                if (type.Length == 0) { unnamed = Math.Max(unnamed, value); continue; }
                string name = GpuInstanceName.FriendlyEngine(type);
                engines[name] = Math.Max(engines.GetValueOrDefault(name), value);
            }

            // Drivers that don't report temperature fail every time; stop asking after the first miss.
            double? temperature = null;
            if (kmt != 0 && !_noTemperature.Contains(adapter.Index))
            {
                temperature = D3dkmt.Temperature(kmt);
                if (temperature is null) _noTemperature.Add(adapter.Index);
            }

            samples.Add(new GpuSample(
                adapter,
                engines,
                Math.Max(unnamed, engines.Count == 0 ? 0 : engines.Values.Max()),
                dedicated.GetValueOrDefault(adapter.LuidKey.ToLowerInvariant()),
                shared.GetValueOrDefault(adapter.LuidKey.ToLowerInvariant()),
                temperature));
        }
        return samples;
    }

    private Dictionary<string, ulong> ReadMemory(PdhCounter? counter)
    {
        var d = new Dictionary<string, ulong>();
        _items.Clear();
        counter?.ReadArray(_items);
        foreach (var item in _items)
        {
            if (!GpuInstanceName.TryParseAdapter(item.Instance, out var luid)) continue;
            luid = luid.ToLowerInvariant();
            d[luid] = d.GetValueOrDefault(luid) + (ulong)Math.Max(0, item.Value);
        }
        return d;
    }

    public void Dispose()
    {
        _query.Dispose();
        foreach (var (_, kmt) in _adapters) D3dkmt.Close(kmt);
    }
}

public static partial class GpuInstanceName
{
    public static string LuidKey(int high, uint low) =>
        $"0x{unchecked((uint)high):X8}_0x{low:X8}";

    /// <summary>Parses "pid_1234_luid_0x00000000_0x0000D1E3_phys_0_eng_3_engtype_3D". Unnamed engines yield an empty type.</summary>
    public static bool TryParseEngine(string instance, out string luid, out int phys, out int eng, out string engType)
    {
        var m = EngineRegex().Match(instance);
        if (!m.Success)
        {
            luid = engType = "";
            phys = eng = 0;
            return false;
        }
        luid = m.Groups[1].Value;
        phys = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        eng = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        engType = m.Groups[4].Value;
        return true;
    }

    /// <summary>Parses "luid_0x00000000_0x0000D1E3_phys_0".</summary>
    public static bool TryParseAdapter(string instance, out string luid)
    {
        var m = AdapterRegex().Match(instance);
        luid = m.Success ? m.Groups[1].Value : "";
        return m.Success;
    }

    /// <summary>"VideoDecode" → "Video Decode"; every "Compute_N" collapses into "Compute".</summary>
    public static string FriendlyEngine(string engType)
    {
        if (engType.StartsWith("Compute", StringComparison.OrdinalIgnoreCase)) return "Compute";
        var spaced = CamelRegex().Replace(engType.Replace('_', ' '), " ");
        return spaced.Trim();
    }

    [GeneratedRegex(@"luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_(\d+)_eng_(\d+)_engtype_(.*)$")]
    private static partial Regex EngineRegex();

    [GeneratedRegex(@"luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_\d+")]
    private static partial Regex AdapterRegex();

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelRegex();
}

public static class GpuInfoReader
{
    public static GpuInfo Load(GpuAdapter adapter)
    {
        uint kmt = D3dkmt.Open(adapter.LuidLow, adapter.LuidHigh);
        D3dkmt.ADAPTERADDRESS? address;
        int? wddm;
        try
        {
            address = D3dkmt.Address(kmt);
            wddm = D3dkmt.WddmVersion(kmt);
        }
        finally
        {
            D3dkmt.Close(kmt);
        }

        string venDev = $"VEN_{adapter.VendorId:X4}&DEV_{adapter.DeviceId:X4}";
        string manufacturer = VendorName(adapter.VendorId), driverVersion = "", driverDate = "", location = "",
            currentLink = "", maxLink = "", hardwareId = venDev;

        SetupApi.ForEachDevice(SetupApi.DisplayClass, device =>
        {
            bool match;
            if (address is { } a && device.UInt32(SetupApi.BusNumber) is { } bus && device.UInt32(SetupApi.Address) is { } addr)
                match = bus == a.BusNumber && addr == ((a.DeviceNumber << 16) | a.FunctionNumber);
            else
                match = device.StringList(SetupApi.HardwareIds).Any(id => id.Contains(venDev, StringComparison.OrdinalIgnoreCase));
            if (!match) return false;

            if (device.String(SetupApi.Manufacturer) is { Length: > 0 } m && !m.StartsWith('(')) manufacturer = m;
            driverVersion = device.String(SetupApi.DriverVersion) ?? "";
            driverDate = device.FileTime(SetupApi.DriverDate)?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? "";
            location = device.String(SetupApi.LocationInfo) ?? "";
            (currentLink, maxLink) = SlotLink(device.DevInst);
            return true;
        });

        if (location == "" && address is { } ad)
            location = $"PCI bus {ad.BusNumber}, device {ad.DeviceNumber}, function {ad.FunctionNumber}";

        string wddmText = wddm is { } w ? $"WDDM {w / 1000}.{w % 1000 / 100}" : "";
        return new GpuInfo(manufacturer, driverVersion, driverDate, wddmText, location, currentLink, maxLink, hardwareId);
    }

    /// <summary>
    /// Link of the GPU function. Cards such as Intel Arc sit behind an on-board PCIe switch: the GPU then
    /// reports the switch's internal link (often "Gen 1 x1"), and Windows exposes no link properties for the
    /// switch ports, so the slot link can't be read without a driver. That case is labelled instead.
    /// </summary>
    private static (string Current, string Max) SlotLink(uint devInst)
    {
        string current = Link(SetupApi.NodeUInt32(devInst, SetupApi.PciCurrentLinkSpeed), SetupApi.NodeUInt32(devInst, SetupApi.PciCurrentLinkWidth));
        string max = Link(SetupApi.NodeUInt32(devInst, SetupApi.PciMaxLinkSpeed), SetupApi.NodeUInt32(devInst, SetupApi.PciMaxLinkWidth));

        int pciAncestors = 0;
        for (uint node = SetupApi.Parent(devInst); node != 0 && pciAncestors < 8; node = SetupApi.Parent(node))
        {
            if (!SetupApi.InstanceId(node).StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) break;
            pciAncestors++;
        }

        // A root port alone is one PCI ancestor; more means a switch sits between the slot and the GPU.
        if (pciAncestors >= 3 && current != "")
        {
            const string note = " (internal link behind on-card PCIe switch)";
            return (current + note, max + note);
        }
        return (current, max);
    }

    internal static string Link(uint? speed, uint? width)
    {
        if (speed is null or 0 || width is null or 0) return "";
        string rate = speed switch
        {
            1 => "2.5 GT/s",
            2 => "5 GT/s",
            3 => "8 GT/s",
            4 => "16 GT/s",
            5 => "32 GT/s",
            6 => "64 GT/s",
            _ => "",
        };
        return $"PCIe Gen {speed} x{width}" + (rate == "" ? "" : $" ({rate})");
    }

    public static string VendorName(uint vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x5143 or 0x4D4F4351 => "Qualcomm",
        0x1414 => "Microsoft",
        0x106B => "Apple",
        0x13B5 => "ARM",
        _ => $"Vendor 0x{vendorId:X4}",
    };
}
