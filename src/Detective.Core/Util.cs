using System.Globalization;

namespace Detective.Core;

/// <summary>Fixed-capacity history of chart samples; index 0 is the oldest. Not thread-safe (UI thread only).</summary>
public sealed class RingBuffer
{
    public const int DefaultCapacity = 60;

    private readonly float[] _data;
    private int _start;

    public RingBuffer(int capacity = DefaultCapacity) => _data = new float[capacity];

    public int Capacity => _data.Length;
    public int Count { get; private set; }

    public float this[int index] => _data[(_start + index) % _data.Length];

    public float Latest => Count == 0 ? 0 : this[Count - 1];

    public void Add(float value)
    {
        if (Count < _data.Length)
        {
            _data[(_start + Count) % _data.Length] = value;
            Count++;
        }
        else
        {
            _data[_start] = value;
            _start = (_start + 1) % _data.Length;
        }
    }

    public float Max()
    {
        float max = 0;
        for (int i = 0; i < Count; i++) max = Math.Max(max, this[i]);
        return max;
    }
}

/// <summary>Picks "nice" chart ceilings so auto-scaled axes read cleanly.</summary>
public static class NiceScale
{
    private static readonly double[] BinarySteps = [1, 2, 5, 10, 20, 50, 100, 200, 500];

    /// <summary>1-2-5 × 10ⁿ ceiling, never below <paramref name="minimum"/>. Used for bit rates.</summary>
    public static double Decimal(double value, double minimum)
    {
        if (value <= minimum) return minimum;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double f = value / exp;
        double nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nf * exp;
    }

    /// <summary>{1,2,5,10,20,50,100,200,500} × 1024ⁿ ceiling. Used for byte rates so labels read "100 MB/s".</summary>
    public static double Binary(double value, double minimum)
    {
        if (value <= minimum) return minimum;
        double unit = 1;
        while (value / unit >= 1024) unit *= 1024;
        double f = value / unit;
        foreach (var step in BinarySteps)
            if (f <= step) return step * unit;
        return 1024 * unit;
    }
}

public static class Format
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB", "PB"];
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    private static string Number(double v) =>
        v.ToString(v < 100 && v != Math.Floor(v) ? "0.#" : "0", Culture);

    public static string Bytes(double bytes)
    {
        int u = 0;
        while (bytes >= 1024 && u < ByteUnits.Length - 1) { bytes /= 1024; u++; }
        return $"{Number(bytes)} {ByteUnits[u]}";
    }

    /// <summary>Byte rate, starting at KB/s as Task Manager does.</summary>
    public static string ByteRate(double bytesPerSec)
    {
        double v = bytesPerSec / 1024;
        int u = 1;
        while (v >= 1024 && u < ByteUnits.Length - 1) { v /= 1024; u++; }
        return $"{Number(v)} {ByteUnits[u]}/s";
    }

    public static string BitRate(double bitsPerSec)
    {
        string[] units = ["Kbps", "Mbps", "Gbps", "Tbps"];
        double v = bitsPerSec / 1000;
        int u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
        return $"{Number(v)} {units[u]}";
    }

    public static string Mhz(double mhz) => mhz >= 1000
        ? (mhz / 1000).ToString("0.00", Culture) + " GHz"
        : mhz.ToString("0", Culture) + " MHz";

    public static string Percent(double p) => p.ToString("0", Culture) + "%";

    public static string Uptime(TimeSpan t) =>
        $"{(int)t.TotalDays}:{t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";

    public static string Count(long n) => n.ToString("N0", Culture);
}

/// <summary>Small WMI/CIM helper. Only used for static info, never in the sampling loop.</summary>
public static class Wmi
{
    public const string Cimv2 = @"root\cimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string StandardCimv2 = @"root\StandardCimv2";

    /// <summary>Runs an explicit "SELECT a, b FROM ..." query; returns an empty list on any failure.</summary>
    public static List<WmiObject> Query(string scope, string wql) =>
        Native.WmiClient.Query(scope, wql).Select(row => new WmiObject(row)).ToList();
}

/// <summary>One WMI result row.</summary>
public sealed class WmiObject(Dictionary<string, object?> values)
{
    public object? Get(string name) => values.GetValueOrDefault(name);

    public string Str(string name) => (Get(name)?.ToString() ?? "").Trim();

    public ulong U64(string name) => Get(name) switch
    {
        long l => (ulong)l,
        string s when ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
        _ => 0,
    };

    public bool Bool(string name) => Get(name) is true;
}
