using Detective.Core.Native;

namespace Detective.Core;

/// <summary>One physical disk; its mounted volumes are listed in <see cref="Letters"/>.</summary>
public sealed record DiskSample(
    int Number,
    string Letters,
    double ActivePercent,
    double ReadBytesPerSec,
    double WriteBytesPerSec,
    double AvgResponseMs);

public sealed record DiskInfo(
    int Number,
    string Model,
    string Manufacturer,
    string Interface,
    string MediaType,
    ulong Size,
    string Firmware,
    bool IsSystem,
    bool IsBoot);

public sealed record VolumeInfo(
    string Letter,
    string Label,
    string FileSystem,
    ulong Capacity,
    ulong Free,
    bool HasPageFile,
    bool IsSystem);

public sealed class DiskMonitor : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhCounter? _idle, _read, _write, _transfer;
    private readonly List<PdhItem> _items = new();

    public DiskMonitor()
    {
        _idle = _query.TryAdd(@"\PhysicalDisk(*)\% Idle Time");
        _read = _query.TryAdd(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _write = _query.TryAdd(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        _transfer = _query.TryAdd(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
        _query.Collect();
    }

    public IReadOnlyList<DiskSample> Sample()
    {
        _query.Collect();
        var idle = ReadAll(_idle);
        var read = ReadAll(_read);
        var write = ReadAll(_write);
        var transfer = ReadAll(_transfer);

        var disks = new List<DiskSample>();
        foreach (var (instance, idleValue) in idle)
        {
            if (!DiskInstance.TryParse(instance, out int number, out string letters)) continue;
            disks.Add(new DiskSample(
                number,
                letters,
                Math.Clamp(100 - idleValue, 0, 100),
                read.GetValueOrDefault(instance),
                write.GetValueOrDefault(instance),
                transfer.GetValueOrDefault(instance) * 1000));
        }
        disks.Sort((a, b) => a.Number.CompareTo(b.Number));
        return disks;
    }

    private Dictionary<string, double> ReadAll(PdhCounter? counter)
    {
        _items.Clear();
        counter?.ReadArray(_items);
        var d = new Dictionary<string, double>(_items.Count);
        foreach (var item in _items) d[item.Instance] = item.Value;
        return d;
    }

    public void Dispose() => _query.Dispose();
}

internal static class DiskInstance
{
    /// <summary>Parses PhysicalDisk instances such as "0 C: D:" into the disk number and its drive letters.</summary>
    public static bool TryParse(string instance, out int number, out string letters)
    {
        letters = "";
        var parts = instance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out number))
        {
            number = -1;
            return false;
        }
        letters = string.Join(' ', parts.Skip(1));
        return true;
    }
}

public static class DiskInfoReader
{
    /// <summary>Anything above 1 PiB on a single physical disk is treated as a bogus report.</summary>
    public const ulong PlausibleMaxSize = 1UL << 50;

    public static DiskInfo Load(int number)
    {
        string model = "", manufacturer = "", bus = "", media = "", firmware = "";
        ulong size = 0;

        var pd = Wmi.Query(Wmi.Storage,
            $"SELECT FriendlyName, Manufacturer, Model, BusType, MediaType, Size, FirmwareVersion FROM MSFT_PhysicalDisk WHERE DeviceId = '{number}'")
            .FirstOrDefault();
        if (pd is not null)
        {
            model = pd.Str("FriendlyName") is { Length: > 0 } fn ? fn : pd.Str("Model");
            manufacturer = pd.Str("Manufacturer");
            bus = BusType((uint)pd.U64("BusType"));
            media = MediaType((uint)pd.U64("MediaType"));
            size = pd.U64("Size");
            firmware = pd.Str("FirmwareVersion");
        }
        else if (Wmi.Query(Wmi.Cimv2, $"SELECT Model, InterfaceType, Size, FirmwareRevision FROM Win32_DiskDrive WHERE Index = {number}")
                     .FirstOrDefault() is { } dd)
        {
            model = dd.Str("Model");
            bus = dd.Str("InterfaceType");
            size = dd.U64("Size");
            firmware = dd.Str("FirmwareRevision");
        }

        if (media == "" && bus == "NVMe") media = "SSD";
        // Vendor fields are often blank, generic or a model prefix ("WDC WD40"): normalise when we recognise it.
        manufacturer = GuessManufacturer(manufacturer) is { Length: > 0 } fromField ? fromField
            : GuessManufacturer(model) is { Length: > 0 } fromModel ? fromModel
            : manufacturer.StartsWith('(') ? "" : manufacturer;

        // Some USB bridges report nonsense sizes through MSFT_PhysicalDisk; MSFT_Disk's size is the usable one.
        var disk = Wmi.Query(Wmi.Storage, $"SELECT IsSystem, IsBoot, Size FROM MSFT_Disk WHERE Number = {number}").FirstOrDefault();
        if (disk?.U64("Size") is > 0 and var diskSize) size = diskSize;
        // Multi-bay USB enclosures (e.g. JMicron bridges) can report petabyte-scale garbage through every API.
        if (size > PlausibleMaxSize) size = 0;

        return new DiskInfo(number, model, manufacturer, bus, media, size, firmware,
            disk?.Bool("IsSystem") ?? false, disk?.Bool("IsBoot") ?? false);
    }

    public static List<VolumeInfo> LoadVolumes(string letters)
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var list = new List<VolumeInfo>();
        foreach (var letter in letters.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var root = letter + "\\";
                var d = new DriveInfo(root);
                if (!d.IsReady) continue;
                list.Add(new VolumeInfo(letter, d.VolumeLabel, d.DriveFormat, (ulong)d.TotalSize, (ulong)d.AvailableFreeSpace,
                    File.Exists(Path.Combine(root, "pagefile.sys")),
                    root.Equals(system, StringComparison.OrdinalIgnoreCase)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }
        return list;
    }

    internal static string BusType(uint bus) => bus switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "Unknown",
    };

    internal static string MediaType(uint media) => media switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "",
    };

    internal static string GuessManufacturer(string model)
    {
        (string Prefix, string Name)[] known =
        [
            ("Samsung", "Samsung"), ("WDC", "Western Digital"), ("WD", "Western Digital"),
            ("Western Digital", "Western Digital"), ("CT", "Crucial"), ("Crucial", "Crucial"),
            ("Kingston", "Kingston"), ("SK hynix", "SK hynix"), ("SKHynix", "SK hynix"), ("Seagate", "Seagate"),
            ("ST", "Seagate"), ("INTEL", "Intel"), ("Micron", "Micron"), ("Sabrent", "Sabrent"),
            ("TOSHIBA", "Toshiba"), ("KIOXIA", "KIOXIA"), ("ADATA", "ADATA"), ("Corsair", "Corsair"),
            ("Lexar", "Lexar"), ("TEAM", "Team Group"), ("SanDisk", "SanDisk"), ("Hitachi", "Hitachi"),
            ("HGST", "HGST"), ("Phison", "Phison"), ("Solidigm", "Solidigm"), ("PNY", "PNY"),
        ];
        foreach (var (prefix, name) in known)
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name;
        return "";
    }
}
