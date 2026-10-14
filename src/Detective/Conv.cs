using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Detective;

/// <summary>Functions for x:Bind.</summary>
public static class Conv
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Selected tile: a tint of the item's accent plus an accent outline.</summary>
    public static Brush TileBackground(bool selected, Color accent) =>
        new SolidColorBrush(selected ? ColorHelper.FromArgb(0x26, accent.R, accent.G, accent.B) : Colors.Transparent);

    public static Brush TileBorder(bool selected, Color accent) =>
        new SolidColorBrush(selected ? accent : Colors.Transparent);

    // Process table cells are tinted with the resource's accent in proportion to use, like Task Manager's
    // heat map. Brushes are cached per step: rows rebind every tick.
    private static readonly Dictionary<(Color, int), Brush> HeatBrushes = new();

    private static Brush Heat(double fraction, Color accent)
    {
        int step = fraction < 0.005 ? 0 : (int)Math.Ceiling(Math.Clamp(fraction, 0, 1) * 10);
        if (!HeatBrushes.TryGetValue((accent, step), out var brush))
        {
            byte alpha = step == 0 ? (byte)0 : (byte)(0x10 + step * 0x0B);
            HeatBrushes[(accent, step)] = brush = new SolidColorBrush(ColorHelper.FromArgb(alpha, accent.R, accent.G, accent.B));
        }
        return brush;
    }

    public static Brush CpuHeat(double fraction) => Heat(fraction, ViewModels.Accents.Cpu);

    public static Brush MemoryHeat(double fraction) => Heat(fraction, ViewModels.Accents.Memory);

    public static Brush DiskHeat(double fraction) => Heat(fraction, ViewModels.Accents.Disk);

    public static Brush NetworkHeat(double fraction) => Heat(fraction, ViewModels.Accents.Network);

    public static Brush GpuHeat(double fraction) => Heat(fraction, ViewModels.Accents.Gpu);

    public static double Opacity(bool visible) => visible ? 1 : 0;

    public static Windows.UI.Text.FontWeight Weight(bool bold) =>
        bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    /// <summary>Tree expander chevron: right when collapsed, down when expanded.</summary>
    public static string Chevron(bool expanded) => expanded ? "\uE70D" : "\uE76C";
}
