using System.Text.Json;
using System.Text.Json.Serialization;

namespace Detective;

/// <summary>User settings, stored as JSON in %LOCALAPPDATA%\Detective (the app is unpackaged).</summary>
public sealed class AppSettings
{
    public static readonly double[] Intervals = [0.5, 1, 4];

    /// <summary>0 = High, 1 = Normal, 2 = Low, 3 = Paused.</summary>
    public int SpeedIndex { get; set; } = 1;

    /// <summary>0 = System, 1 = Light, 2 = Dark.</summary>
    public int ThemeIndex { get; set; }

    /// <summary>Last focus item as "Kind:PersistKey" (see FocusItemVm.PersistKey).</summary>
    public string? LastFocus { get; set; }

    /// <summary>CPU view shows the per-logical-processor grid instead of overall utilization.</summary>
    public bool CpuShowLogical { get; set; }

    /// <summary>Highest send/receive rates seen per network adapter (interface GUID), in bits per second.</summary>
    public Dictionary<string, NetworkPeak> NetworkPeaks { get; set; } = new();

    /// <summary>GPU engine picked in each of the four charts, keyed by the GPU's persist key.</summary>
    public Dictionary<string, string[]> GpuEngines { get; set; } = new();

    public int Width { get; set; } = 1280;

    public int Height { get; set; } = 900;

    /// <summary>False in snapshot mode so diagnostic runs never overwrite the user's settings.</summary>
    [JsonIgnore]
    public bool Persist { get; set; } = true;

    [JsonIgnore]
    public bool Paused => SpeedIndex >= Intervals.Length;

    [JsonIgnore]
    public TimeSpan Interval => TimeSpan.FromSeconds(Intervals[Math.Clamp(SpeedIndex, 0, Intervals.Length - 1)]);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Detective", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.AppSettings) ?? new AppSettings();
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        if (!Persist) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class NetworkPeak
{
    public double SendBitsPerSec { get; set; }

    public double ReceiveBitsPerSec { get; set; }
}

/// <summary>Source-generated serializer: reflection-based JSON doesn't survive trimming / Native AOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
