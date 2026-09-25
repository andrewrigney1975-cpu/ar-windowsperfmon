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
}
