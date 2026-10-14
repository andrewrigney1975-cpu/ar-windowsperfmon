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

public class ProcessTests
{
    private static ProcessSample P(int pid, long created, int parent, bool window = false, double cpu = 0) =>
        new(new ProcessKey(pid, created), parent, $"p{pid}.exe", null, null, cpu, 0, 0, null, 0, window);

    [Theory]
    [InlineData(10_000_000, 1.0, 1, 100)]   // one full core-second on one core
    [InlineData(10_000_000, 1.0, 4, 25)]
    [InlineData(5_000_000, 0.5, 2, 50)]
    [InlineData(0, 1.0, 4, 0)]
    [InlineData(-5, 1.0, 4, 0)]
    [InlineData(10_000_000, 0, 4, 0)]
    public void CpuPercentIsShareOfAllProcessors(long delta, double seconds, int processors, double expected) =>
        Assert.Equal(expected, ProcessMonitor.CpuPercent(delta, seconds, processors), 6);

    [Fact]
    public void ReusedParentPidIsNotAParent()
    {
        // Process 10 started after 20, so it can't be 20's parent: 20's real parent exited and 10 reused the PID.
        var (_, background) = ProcessTree.Group([P(10, 500, 1), P(20, 100, 10)]);
        Assert.Equal(2, background.Count);
        Assert.All(background, n => Assert.Empty(n.Children));
    }

    [Fact]
    public void AppsHoldTheirWindowlessDescendantsAndWindowedChildrenSplitOff()
    {
        var samples = new[]
        {
            P(1, 1, 0),                    // background root
            P(2, 2, 1, window: true),      // app
            P(3, 3, 2),                    // helper of app 2
            P(4, 4, 3),                    // grandchild of app 2
            P(5, 5, 2, window: true),      // window started by app 2: its own app
            P(6, 6, 5),                    // helper of app 5
            P(7, 7, 1),                    // background child
        };
        var (apps, background) = ProcessTree.Group(samples);

        Assert.Equal([2, 5], apps.Select(a => a.Sample.Pid).Order());
        var app2 = apps.Single(a => a.Sample.Pid == 2);
        Assert.Equal(3, Assert.Single(app2.Children).Sample.Pid);
        Assert.Equal(4, Assert.Single(app2.Children[0].Children).Sample.Pid);
        Assert.Equal(6, Assert.Single(apps.Single(a => a.Sample.Pid == 5).Children).Sample.Pid);

        var root = Assert.Single(background);
        Assert.Equal(1, root.Sample.Pid);
        Assert.Equal(7, Assert.Single(root.Children).Sample.Pid);
    }

    [Fact]
    public void TreeOfEndsRootFirstThenLeavesUpwards()
    {
        var entries = new List<ProcessEntry>
        {
            new(1, 0, 1, "root", 1, 0, 0, 0, 0),
            new(2, 1, 2, "child", 1, 0, 0, 0, 0),
            new(3, 2, 3, "grandchild", 1, 0, 0, 0, 0),
            new(4, 1, 0, "reused pid, older than root", 1, 0, 0, 0, 0),
        };
        var order = ProcessTree.TreeOf(entries, new ProcessKey(1, 1)).Select(k => k.Pid);
        Assert.Equal([1, 3, 2], order);
    }

    [Fact]
    public void GpuEnginePid()
    {
        Assert.True(GpuInstanceName.TryParsePid("pid_1234_luid_0x00000000_0x0000D1E3_phys_0_eng_3_engtype_3D", out int pid));
        Assert.Equal(1234, pid);
        Assert.False(GpuInstanceName.TryParsePid("luid_0x00000000_0x0000D1E3_phys_0", out _));
    }

    [Fact]
    public void LiveSampleReadsThisProcess()
    {
        using var monitor = new ProcessMonitor();
        monitor.Sample(null);
        Thread.Sleep(200);
        var list = monitor.Sample(null);

        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var me = Assert.Single(list.Items, p => p.Pid == Environment.ProcessId);
        Assert.Equal(self.StartTime.ToFileTime(), me.Key.CreateTime);
        Assert.Equal(self.ProcessName + ".exe", me.Name, ignoreCase: true);
        Assert.InRange(me.Memory, 1UL << 20, (ulong)self.WorkingSet64);
        Assert.NotNull(me.Path);
        Assert.Contains(list.Items, p => p.Pid == me.ParentPid);
        Assert.True(list.TotalPhysicalMemory > 0);
    }
}
