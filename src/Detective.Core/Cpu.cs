using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;
using Detective.Core.Native;
using Microsoft.Win32;

namespace Detective.Core;

public sealed record CpuSample(
    double Utilization,
    double[] PerLogical,
    double SpeedMhz,
    int Processes,
    int Threads,
    int Handles,
    TimeSpan Uptime);

public sealed record CpuInfo(
    string Name,
    string Vendor,
    double BaseMhz,
    int Sockets,
    int Cores,
    int LogicalProcessors,
    ulong L1Cache,
    ulong L2Cache,
    ulong L3Cache,
    string Family,
    string Model,
    string Stepping,
    bool Virtualization,
    bool HypervisorPresent);

public sealed class CpuMonitor : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhCounter? _utility;
    private readonly PdhCounter? _performance;
    private readonly List<PdhItem> _items = new();
    private readonly double _baseMhz = CpuInfoReader.BaseMhz();

    public CpuMonitor()
    {
        // "% Processor Utility" is what Task Manager charts; fall back to "% Processor Time" on older builds.
        _utility = _query.TryAdd(@"\Processor Information(*)\% Processor Utility")
                   ?? _query.TryAdd(@"\Processor Information(*)\% Processor Time");
        _performance = _query.TryAdd(@"\Processor Information(_Total)\% Processor Performance");
        _query.Collect();
    }

    public CpuSample Sample()
    {
        _query.Collect();
        _items.Clear();
        _utility?.ReadArray(_items);

        double total = 0;
        var logical = new List<(int Group, int Index, double Value)>(_items.Count);
        foreach (var item in _items)
        {
            if (item.Instance == "_Total") total = item.Value;
            else if (CpuInstance.TryParse(item.Instance, out int g, out int i)) logical.Add((g, i, item.Value));
        }
        logical.Sort((a, b) => a.Group != b.Group ? a.Group.CompareTo(b.Group) : a.Index.CompareTo(b.Index));

        double speed = (_performance?.Read() ?? 0) * _baseMhz / 100;
        var pi = Kernel32.PerformanceInfo();

        return new CpuSample(
            Math.Clamp(total, 0, 100),
            logical.Select(l => Math.Clamp(l.Value, 0, 100)).ToArray(),
            speed,
            (int)(pi?.ProcessCount ?? 0),
            (int)(pi?.ThreadCount ?? 0),
            (int)(pi?.HandleCount ?? 0),
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    public void Dispose() => _query.Dispose();
}

internal static class CpuInstance
{
    /// <summary>Parses "group,index" instances of the Processor Information object; rejects totals.</summary>
    public static bool TryParse(string instance, out int group, out int index)
    {
        group = index = 0;
        int comma = instance.IndexOf(',');
        return comma > 0
               && int.TryParse(instance.AsSpan(0, comma), out group)
               && int.TryParse(instance.AsSpan(comma + 1), out index);
    }
}

public static partial class CpuInfoReader
{
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public static double BaseMhz()
    {
        using var key = Registry.LocalMachine.OpenSubKey(CpuKey);
        return key?.GetValue("~MHz") is int mhz ? mhz : 0;
    }

    public static CpuInfo Load()
    {
        using var key = Registry.LocalMachine.OpenSubKey(CpuKey);
        string name = (key?.GetValue("ProcessorNameString") as string ?? "Unknown processor").Trim();
        string vendor = VendorName(key?.GetValue("VendorIdentifier") as string ?? "");
        var (family, model, stepping) = ParseIdentifier(key?.GetValue("Identifier") as string ?? "");

        var topology = ReadTopology();
        bool hypervisor = X86Base.IsSupported && ((X86Base.CpuId(1, 0).Ecx >> 31) & 1) == 1;
        bool virt = Kernel32.IsProcessorFeaturePresent(Kernel32.PF_VIRT_FIRMWARE_ENABLED) || hypervisor;

        return new CpuInfo(name, vendor, BaseMhz(), topology.Sockets, topology.Cores, Environment.ProcessorCount,
            topology.L1, topology.L2, topology.L3, family, model, stepping, virt, hypervisor);
    }

    internal static string VendorName(string id) => id switch
    {
        "GenuineIntel" => "Intel",
        "AuthenticAMD" => "AMD",
        "" => "Unknown",
        _ when id.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) => "Qualcomm",
        _ => id,
    };

    /// <summary>Parses "Intel64 Family 6 Model 183 Stepping 1" or "ARMv8 (64-bit) Family 8 Model D4B Revision 0".</summary>
    internal static (string Family, string Model, string Stepping) ParseIdentifier(string identifier)
    {
        var m = IdentifierRegex().Match(identifier);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value) : ("", "", "");
    }

    [GeneratedRegex(@"Family\s+(\w+)\s+Model\s+(\w+)\s+(?:Stepping|Revision)\s+(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex IdentifierRegex();

    private static (int Sockets, int Cores, ulong L1, ulong L2, ulong L3) ReadTopology()
    {
        uint length = 0;
        Kernel32.GetLogicalProcessorInformation(IntPtr.Zero, ref length);
        if (length == 0) return (1, Environment.ProcessorCount, 0, 0, 0);

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!Kernel32.GetLogicalProcessorInformation(buffer, ref length))
                return (1, Environment.ProcessorCount, 0, 0, 0);

            // SYSTEM_LOGICAL_PROCESSOR_INFORMATION: ULONG_PTR mask; enum Relationship; union (16 bytes).
            int unionOffset = IntPtr.Size == 8 ? 16 : 8;
            int stride = unionOffset + 16;
            int sockets = 0, cores = 0;
            ulong l1 = 0, l2 = 0, l3 = 0;
            for (int offset = 0; offset + stride <= length; offset += stride)
            {
                var entry = buffer + offset;
                int relationship = Marshal.ReadInt32(entry, IntPtr.Size);
                switch (relationship)
                {
                    case 0: cores++; break;
                    case 3: sockets++; break;
                    case 2:
                        byte level = Marshal.ReadByte(entry, unionOffset);
                        ulong size = (uint)Marshal.ReadInt32(entry, unionOffset + 4);
                        if (level == 1) l1 += size;
                        else if (level == 2) l2 += size;
                        else if (level == 3) l3 += size;
                        break;
                }
            }
            return (Math.Max(1, sockets), cores, l1, l2, l3);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
