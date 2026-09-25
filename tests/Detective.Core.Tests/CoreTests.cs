using Detective.Core;
using Detective.Core.Native;

namespace Detective.Core.Tests;

public class RingBufferTests
{
    [Fact]
    public void KeepsOldestFirstAndDropsOverflow()
    {
        var rb = new RingBuffer(3);
        foreach (var v in new[] { 1f, 2f, 3f, 4f, 5f }) rb.Add(v);

        Assert.Equal(3, rb.Count);
        Assert.Equal([3f, 4f, 5f], Enumerable.Range(0, rb.Count).Select(i => rb[i]));
        Assert.Equal(5f, rb.Latest);
        Assert.Equal(5f, rb.Max());
    }

    [Fact]
    public void EmptyBufferIsZero()
    {
        var rb = new RingBuffer();
        Assert.Equal(0, rb.Count);
        Assert.Equal(0f, rb.Latest);
        Assert.Equal(0f, rb.Max());
    }
}

public class NiceScaleTests
{
    [Theory]
    [InlineData(0, 100, 100)]
    [InlineData(120, 100, 200)]
    [InlineData(450, 100, 500)]
    [InlineData(501, 100, 1000)]
    [InlineData(2_500_000, 100_000, 5_000_000)]
    public void DecimalRoundsUpToOneTwoFive(double value, double min, double expected) =>
        Assert.Equal(expected, NiceScale.Decimal(value, min));

    [Theory]
    [InlineData(10, 1024, 1024)]
    [InlineData(1500, 1024, 2048)]
    [InlineData(3 * 1024 * 1024, 1024, 5 * 1024 * 1024)]
    [InlineData(600 * 1024 * 1024, 1024, 1024L * 1024 * 1024)]
    public void BinaryRoundsUpInPowersOf1024(double value, double min, double expected) =>
        Assert.Equal(expected, NiceScale.Binary(value, min));
}

public class ParserTests
{
    [Theory]
    [InlineData("0,5", true, 0, 5)]
    [InlineData("1,12", true, 1, 12)]
    [InlineData("0,_Total", false, 0, 0)]
    [InlineData("_Total", false, 0, 0)]
    public void ProcessorInstances(string instance, bool ok, int group, int index)
    {
        Assert.Equal(ok, CpuInstance.TryParse(instance, out int g, out int i));
        if (ok)
        {
            Assert.Equal(group, g);
            Assert.Equal(index, i);
        }
    }

    [Theory]
    [InlineData("0 C: F:", true, 0, "C: F:")]
    [InlineData("7", true, 7, "")]
    [InlineData("_Total", false, -1, "")]
    public void PhysicalDiskInstances(string instance, bool ok, int number, string letters)
    {
        Assert.Equal(ok, DiskInstance.TryParse(instance, out int n, out string l));
        Assert.Equal(number, n);
        Assert.Equal(letters, l);
    }

    [Fact]
    public void GpuEngineInstance()
    {
        Assert.True(GpuInstanceName.TryParseEngine(
            "pid_1234_luid_0x00000000_0x0000E947_phys_0_eng_3_engtype_VideoDecode",
            out var luid, out int phys, out int eng, out var type));
        Assert.Equal("0x00000000_0x0000E947", luid);
        Assert.Equal(0, phys);
        Assert.Equal(3, eng);
        Assert.Equal("VideoDecode", type);
    }

    [Fact]
    public void GpuEngineInstanceWithoutTypeYieldsEmptyType()
    {
        Assert.True(GpuInstanceName.TryParseEngine(
            "pid_4_luid_0x00000000_0x0000E947_phys_0_eng_10_engtype_", out _, out _, out _, out var type));
        Assert.Equal("", type);
    }

    [Fact]
    public void GpuAdapterMemoryInstance()
    {
        Assert.True(GpuInstanceName.TryParseAdapter("luid_0x00000000_0x0000E947_phys_0", out var luid));
        Assert.Equal("0x00000000_0x0000E947", luid);
        Assert.False(GpuInstanceName.TryParseAdapter("_Total", out _));
    }

    [Fact]
    public void LuidKeyMatchesPdhFormat() =>
        Assert.Equal("0x00000000_0x0000E947", GpuInstanceName.LuidKey(0, 0xE947));

    [Theory]
    [InlineData("3D", "3D")]
    [InlineData("VideoDecode", "Video Decode")]
    [InlineData("VideoProcessing", "Video Processing")]
    [InlineData("Compute_0", "Compute")]
    [InlineData("Compute_1", "Compute")]
    [InlineData("Graphics_1", "Graphics 1")]
    [InlineData("GSC", "GSC")]
    public void FriendlyEngineNames(string raw, string expected) =>
        Assert.Equal(expected, GpuInstanceName.FriendlyEngine(raw));

    [Theory]
    [InlineData("Intel64 Family 6 Model 151 Stepping 2", "6", "151", "2")]
    [InlineData("AMD64 Family 25 Model 97 Stepping 2", "25", "97", "2")]
    [InlineData("ARMv8 (64-bit) Family 8 Model D4B Revision 0", "8", "D4B", "0")]
    [InlineData("garbage", "", "", "")]
    public void CpuIdentifier(string id, string family, string model, string stepping) =>
        Assert.Equal((family, model, stepping), CpuInfoReader.ParseIdentifier(id));
}

public class MappingTests
{
    [Theory]
    [InlineData(1u, 16u, "PCIe Gen 1 x16 (2.5 GT/s)")]
    [InlineData(4u, 16u, "PCIe Gen 4 x16 (16 GT/s)")]
    [InlineData(0u, 16u, "")]
    public void PcieLink(uint speed, uint width, string expected) =>
        Assert.Equal(expected, GpuInfoReader.Link(speed, width));

    [Theory]
    [InlineData("WDC WD40EZRZ", "Western Digital")]
    [InlineData("Samsung SSD 990 PRO 2TB", "Samsung")]
    [InlineData("Mystery Disk", "")]
    public void DiskManufacturerGuess(string model, string expected) =>
        Assert.Equal(expected, DiskInfoReader.GuessManufacturer(model));

    [Theory]
    [InlineData("80CE", "Samsung")]
    [InlineData("Corsair", "Corsair")]
    public void MemoryManufacturer(string raw, string expected) =>
        Assert.Equal(expected, MemoryInfoReader.ManufacturerName(raw));

    [Fact]
    public void MemoryCompositionSegmentsSumToTotal()
    {
        var m = new MemorySample(Total: 100, Available: 60, Installed: 104, Committed: 0, CommitLimit: 0,
            PagedPool: 0, NonPagedPool: 0, Modified: 5, Standby: 40, Free: 20);
        Assert.Equal(35UL, m.InUseSegment);
        Assert.Equal(40UL, m.InUse);
        Assert.Equal(45UL, m.Cached);
        Assert.Equal(4UL, m.HardwareReserved);
        Assert.Equal(m.Total, m.InUseSegment + m.Modified + m.Standby + m.Free);
    }

    [Fact]
    public void MacFormatting() => Assert.Equal("F4-26-79-40-D5-8A", NetworkInfoReader.FormatMac("F4267940D58A"));

    [Theory]
    [InlineData(10, "802.11ax (Wi-Fi 6/6E)")]
    [InlineData(99, "Unknown")]
    public void WifiPhyNames(int phy, string expected) => Assert.Equal(expected, WlanClient.PhyName(phy));
}

public class FormatTests
{
    [Fact]
    public void Units()
    {
        Assert.Equal("0 KB/s", Format.ByteRate(0));
        Assert.Equal("1.5 MB/s", Format.ByteRate(1.5 * 1024 * 1024));
        Assert.Equal("8.4 Mbps", Format.BitRate(8_400_000));
        Assert.Equal("4.50 GHz", Format.Mhz(4500).Replace(',', '.'));
        Assert.Equal("1:02:03:04", Format.Uptime(new TimeSpan(1, 2, 3, 4)));
    }
}

public class SustainedLoadTests
{
    private static RingBuffer History(params float[] values)
    {
        var rb = new RingBuffer();
        foreach (var v in values) rb.Add(v);
        return rb;
    }

    [Theory]
    [InlineData(1.0, 5)]
    [InlineData(0.5, 10)]
    [InlineData(4.0, 2)]
    public void FiveSecondsInSamples(double intervalSeconds, int expected) =>
        Assert.Equal(expected, SustainedLoad.SamplesFor(TimeSpan.FromSeconds(intervalSeconds)));

    [Fact]
    public void NeedsTheWholeWindowAboveTheThreshold()
    {
        Assert.Equal(LoadLevel.Normal, SustainedLoad.Level(History(95, 95, 95, 95), 5));      // only 4 s so far
        Assert.Equal(LoadLevel.Critical, SustainedLoad.Level(History(95, 95, 95, 95, 95), 5));
        Assert.Equal(LoadLevel.High, SustainedLoad.Level(History(95, 80, 95, 95, 95), 5));    // one sample 75–90
        Assert.Equal(LoadLevel.Normal, SustainedLoad.Level(History(95, 95, 70, 95, 95), 5));  // one dip below 75
    }

    [Fact]
    public void OnlyTheLatestWindowCounts()
    {
        Assert.Equal(LoadLevel.Normal, SustainedLoad.Level(History(99, 99, 99, 99, 99, 10), 5));
        Assert.Equal(LoadLevel.High, SustainedLoad.Level(History(10, 10, 76, 80, 85, 88, 77), 5));
    }

    [Fact]
    public void ThresholdsAreStrictlyAbove()
    {
        Assert.Equal(LoadLevel.Normal, SustainedLoad.Level(History(75, 75, 75, 75, 75), 5));
        Assert.Equal(LoadLevel.High, SustainedLoad.Level(History(90, 90, 90, 90, 90), 5));
    }
}
