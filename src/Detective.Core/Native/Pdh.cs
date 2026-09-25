using System.Runtime.InteropServices;

namespace Detective.Core.Native;

/// <summary>One PDH query. Counters are added with English paths so lookups work on any locale.</summary>
public sealed class PdhQuery : IDisposable
{
    private IntPtr _handle;
    private readonly List<PdhCounter> _counters = new();

    public PdhQuery()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _handle) != 0)
            throw new InvalidOperationException("PdhOpenQuery failed.");
    }

    /// <summary>Adds a counter, or returns null when the counter does not exist on this machine.</summary>
    public PdhCounter? TryAdd(string englishPath)
    {
        if (PdhAddEnglishCounterW(_handle, englishPath, IntPtr.Zero, out var counter) != 0)
            return null;
        var c = new PdhCounter(counter);
        _counters.Add(c);
        return c;
    }

    public bool Collect() => _handle != IntPtr.Zero && PdhCollectQueryData(_handle) == 0;

    public void Dispose()
    {
        foreach (var c in _counters) c.FreeBuffer();
        _counters.Clear();
        if (_handle != IntPtr.Zero)
        {
            PdhCloseQuery(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    internal const uint Format = PDH_FMT_DOUBLE | PDH_FMT_NOCAP100;
    internal const uint PDH_MORE_DATA = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

public readonly record struct PdhItem(string Instance, double Value);

public sealed class PdhCounter
{
    private readonly IntPtr _handle;
    private IntPtr _buffer;
    private uint _bufferSize;

    internal PdhCounter(IntPtr handle) => _handle = handle;

    /// <summary>Reads a single-instance counter. Null until the counter has valid data (rate counters need two collects).</summary>
    public double? Read()
    {
        if (PdhGetFormattedCounterValue(_handle, PdhQuery.Format, out _, out var value) != 0)
            return null;
        return value.CStatus <= 1 ? value.DoubleValue : null;
    }

    /// <summary>Reads every instance of a wildcard counter, skipping instances without valid data.</summary>
    public void ReadArray(List<PdhItem> into)
    {
        uint size = _bufferSize;
        uint status = PdhGetFormattedCounterArrayW(_handle, PdhQuery.Format, ref size, out uint count, _buffer);
        if (status == PdhQuery.PDH_MORE_DATA)
        {
            FreeBuffer();
            _bufferSize = size + size / 4;
            _buffer = Marshal.AllocHGlobal((int)_bufferSize);
            size = _bufferSize;
            status = PdhGetFormattedCounterArrayW(_handle, PdhQuery.Format, ref size, out count, _buffer);
        }
        if (status != 0) return;

        // PDH_FMT_COUNTERVALUE_ITEM_W: { LPWSTR szName; PDH_FMT_COUNTERVALUE { DWORD CStatus; double } }
        const int stride = 24;
        for (int i = 0; i < count; i++)
        {
            var item = _buffer + i * stride;
            uint cstatus = (uint)Marshal.ReadInt32(item, 8);
            if (cstatus > 1) continue;
            var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
            var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
            into.Add(new PdhItem(name, value));
        }
    }

    internal void FreeBuffer()
    {
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            _bufferSize = 0;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
}
