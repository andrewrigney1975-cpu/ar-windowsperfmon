using System.Runtime.InteropServices.WindowsRuntime;
using Detective.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Detective;

/// <summary>
/// Diagnostic mode: <c>Detective.exe --snapshot &lt;dir&gt;</c> renders each focus view to PNG (via
/// RenderTargetBitmap, so it works while the screen is locked or the window is covered) and exits.
/// </summary>
internal static class SnapshotMode
{
    public static string? Directory { get; } = Arg("--snapshot");

    /// <summary>Optional "light" or "dark" override so both themes can be checked.</summary>
    public static string? Theme { get; } = Arg("--theme");

    /// <summary>Optional window size in DIPs, e.g. "1280x860", instead of the saved size.</summary>
    public static (int Width, int Height)? Size { get; } = ParseSize(Arg("--size"));

    /// <summary>
    /// "--redact": replace identifying network details (SSID, IP addresses, gateway, DNS, MAC) with
    /// documentation placeholders, for screenshots that will be published.
    /// </summary>
    public static bool Redact { get; } = Environment.GetCommandLineArgs().Contains("--redact");

    private static (int, int)? ParseSize(string? value)
    {
        var parts = value?.Split('x');
        return parts is [var w, var h] && int.TryParse(w, out int width) && int.TryParse(h, out int height)
            ? (width, height)
            : null;
    }

    private static string? Arg(string name)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static async Task RunAsync(MainWindow window, FrameworkElement root, ShellVm shell, Action<bool> setCpuLogical)
    {
        System.IO.Directory.CreateDirectory(Directory!);
        // Let history accumulate so charts have shape.
        await Task.Delay(TimeSpan.FromSeconds(12));

        var shots = new List<(string Name, Func<bool> Select)>
        {
            ("1-cpu", () => { shell.SelectKind(FocusKind.Cpu); setCpuLogical(false); return true; }),
            ("2-cpu-logical", () => { setCpuLogical(true); return true; }),
            ("3-memory", () => { setCpuLogical(false); shell.SelectKind(FocusKind.Memory); return true; }),
            ("4-disk", () => Select(shell, FocusKind.Disk)),
            ("5-network", () => Select(shell, FocusKind.Network)),
            ("6-gpu", () => Select(shell, FocusKind.Gpu)),
        };

        foreach (var (name, select) in shots)
        {
            if (!select()) continue;
            await Task.Delay(1500);
            await RenderAsync(root, Path.Combine(Directory!, name + ".png"));
        }
        window.Close();
    }

    private static bool Select(ShellVm shell, FocusKind kind)
    {
        if (shell.Items.All(i => i.Kind != kind)) return false;
        shell.SelectKind(kind);
        return true;
    }

    private static async Task RenderAsync(FrameworkElement root, string path)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(root);
        var pixels = await rtb.GetPixelsAsync();
        double scale = root.XamlRoot?.RasterizationScale ?? 1;

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96 * scale, 96 * scale, pixels.ToArray());
        await encoder.FlushAsync();

        stream.Seek(0);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        await File.WriteAllBytesAsync(path, bytes);
    }

    /// <summary>RenderTargetBitmap can't see the Mica backdrop, so paint Mica's approximate base colour instead.</summary>
    public static void PrepareBackground(FrameworkElement root)
    {
        if (root is not Microsoft.UI.Xaml.Controls.Panel panel) return;
        void Paint() => panel.Background = new SolidColorBrush(root.ActualTheme == ElementTheme.Light
            ? Microsoft.UI.ColorHelper.FromArgb(255, 0xF3, 0xF3, 0xF3)
            : Microsoft.UI.ColorHelper.FromArgb(255, 0x20, 0x20, 0x20));
        root.ActualThemeChanged += (_, _) => Paint();
        Paint();
    }
}
