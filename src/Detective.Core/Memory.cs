using Detective.Core.Native;

namespace Detective.Core;

public sealed record MemorySample(
    ulong Total,
    ulong Available,
    ulong Installed,
    ulong Committed,
    ulong CommitLimit,
    ulong PagedPool,
    ulong NonPagedPool,
    ulong Modified,
    ulong Standby,
    ulong Free)
{
    public ulong InUse => Total - Available;
    public ulong Cached => Standby + Modified;
    public ulong HardwareReserved => Installed > Total ? Installed - Total : 0;

    /// <summary>The "In use" segment of the composition bar: what is left after the three page lists.</summary>
    public ulong InUseSegment
    {
        get
        {
            ulong lists = Modified + Standby + Free;
            return Total > lists ? Total - lists : 0;
        }
    }
}

public sealed record MemoryModule(
    string Slot,
    ulong Capacity,
    uint Speed,
    uint ConfiguredSpeed,
    string Manufacturer,
    string PartNumber,
    string Type,
    string FormFactor,
    uint VoltageMv);

public sealed record MemoryInfo(IReadOnlyList<MemoryModule> Modules, int TotalSlots);

public sealed class MemoryMonitor : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhCounter? _modified, _standbyCore, _standbyNormal, _standbyReserve, _free;
    private readonly ulong _installed;

    public MemoryMonitor()
    {
        _modified = _query.TryAdd(@"\Memory\Modified Page List Bytes");
        _standbyCore = _query.TryAdd(@"\Memory\Standby Cache Core Bytes");
        _standbyNormal = _query.TryAdd(@"\Memory\Standby Cache Normal Priority Bytes");
        _standbyReserve = _query.TryAdd(@"\Memory\Standby Cache Reserve Bytes");
        _free = _query.TryAdd(@"\Memory\Free & Zero Page List Bytes");
        _installed = Kernel32.GetPhysicallyInstalledSystemMemory(out ulong kb) ? kb * 1024 : 0;
    }

    public MemorySample Sample()
    {
        _query.Collect();
        var ms = Kernel32.MemoryStatus();
        var pi = Kernel32.PerformanceInfo();
        ulong page = pi?.PageSize ?? 4096;

        ulong Read(PdhCounter? c) => (ulong)Math.Max(0, c?.Read() ?? 0);
        ulong standby = Read(_standbyCore) + Read(_standbyNormal) + Read(_standbyReserve);

        return new MemorySample(
            ms?.ullTotalPhys ?? 0,
            ms?.ullAvailPhys ?? 0,
            _installed,
            (ulong)(pi?.CommitTotal ?? 0) * page,
            (ulong)(pi?.CommitLimit ?? 0) * page,
            (ulong)(pi?.KernelPaged ?? 0) * page,
            (ulong)(pi?.KernelNonpaged ?? 0) * page,
            Read(_modified),
            standby,
            Read(_free));
    }

    public void Dispose() => _query.Dispose();
}

public static class MemoryInfoReader
{
    public static MemoryInfo Load()
    {
        var modules = Wmi.Query(Wmi.Cimv2,
                "SELECT DeviceLocator, BankLabel, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType, FormFactor, ConfiguredVoltage FROM Win32_PhysicalMemory")
            .Select(o => new MemoryModule(
                o.Str("DeviceLocator"),
                o.U64("Capacity"),
                (uint)o.U64("Speed"),
                (uint)o.U64("ConfiguredClockSpeed"),
                ManufacturerName(o.Str("Manufacturer")),
                o.Str("PartNumber"),
                MemoryType((uint)o.U64("SMBIOSMemoryType")),
                FormFactor((uint)o.U64("FormFactor")),
                (uint)o.U64("ConfiguredVoltage")))
            .ToList();

        int slots = (int)Wmi.Query(Wmi.Cimv2, "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray")
            .Sum(o => (long)o.U64("MemoryDevices"));

        return new MemoryInfo(modules, Math.Max(slots, modules.Count));
    }

    internal static string MemoryType(uint smbios) => smbios switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "",
    };

    internal static string FormFactor(uint f) => f switch
    {
        7 => "Soldered",
        8 => "DIMM",
        12 => "SODIMM",
        _ => "",
    };

    /// <summary>Some firmware reports JEDEC IDs instead of names; map the common ones.</summary>
    internal static string ManufacturerName(string raw)
    {
        string id = raw.Trim().ToUpperInvariant();
        return id switch
        {
            "80CE" or "CE00" or "00CE" => "Samsung",
            "80AD" or "AD00" or "00AD" => "SK hynix",
            "802C" or "2C00" or "002C" => "Micron",
            "0198" or "9801" => "Kingston",
            "029E" or "9E02" => "Corsair",
            "04CB" or "CB04" => "ADATA",
            "059B" or "9B05" => "Crucial",
            "04EF" or "EF04" => "Team Group",
            "04CD" or "CD04" => "G.Skill",
            _ => raw.Trim(),
        };
    }
}
