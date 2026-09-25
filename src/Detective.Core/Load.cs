namespace Detective.Core;

public enum LoadLevel
{
    Normal,

    /// <summary>Above <see cref="SustainedLoad.HighPercent"/> for the whole window.</summary>
    High,

    /// <summary>Above <see cref="SustainedLoad.CriticalPercent"/> for the whole window.</summary>
    Critical,
}

/// <summary>Classifies a utilization history by how long it has stayed above the high/critical thresholds.</summary>
public static class SustainedLoad
{
    public const float HighPercent = 75;
    public const float CriticalPercent = 90;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Consecutive samples needed to cover <see cref="Window"/> at this sampling interval: 5 at 1 s, 10 at 0.5 s,
    /// 2 at 4 s (each sample is the average over the preceding interval).
    /// </summary>
    public static int SamplesFor(TimeSpan interval) =>
        interval <= TimeSpan.Zero ? 1 : Math.Max(1, (int)Math.Ceiling(Window / interval));

    /// <summary>The level every one of the latest <paramref name="samples"/> values stays above.</summary>
    public static LoadLevel Level(RingBuffer history, int samples)
    {
        if (samples < 1 || history.Count < samples) return LoadLevel.Normal;

        float lowest = float.MaxValue;
        for (int i = history.Count - samples; i < history.Count; i++)
            lowest = Math.Min(lowest, history[i]);

        return lowest > CriticalPercent ? LoadLevel.Critical
            : lowest > HighPercent ? LoadLevel.High
            : LoadLevel.Normal;
    }
}
