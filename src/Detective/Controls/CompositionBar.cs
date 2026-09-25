using Detective.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Detective.Controls;

/// <summary>Task Manager's "Memory composition" bar: In use | Modified | Standby | Free.</summary>
public sealed partial class CompositionBar : UserControl
{
    private readonly Grid _grid = new();
    private readonly Border[] _segments = new Border[4];
    private readonly Border _outline = new() { BorderThickness = new Thickness(1) };

    private static readonly string[] Names = ["In use", "Modified", "Standby", "Free"];
    private static readonly byte[] Alphas = [0x90, 0x55, 0x28, 0x00];

    public CompositionBar()
    {
        for (int i = 0; i < 4; i++)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
            var segment = new Border { BorderThickness = new Thickness(0, 0, i < 3 ? 1 : 0, 0) };
            Grid.SetColumn(segment, i);
            _segments[i] = segment;
            _grid.Children.Add(segment);
        }
        _outline.Child = _grid;
        Content = _outline;
        ActualThemeChanged += (_, _) => ApplyAccent();
        ApplyAccent();
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(object), typeof(CompositionBar), new PropertyMetadata(null, (d, _) => ((CompositionBar)d).Update()));

    /// <summary>A <see cref="MemorySample"/>.</summary>
    public object? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(CompositionBar), new PropertyMetadata(Colors.Purple, (d, _) => ((CompositionBar)d).ApplyAccent()));

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private void ApplyAccent()
    {
        var c = ThemeAccent.For(Accent, this);
        _outline.BorderBrush = new SolidColorBrush(c);
        for (int i = 0; i < 4; i++)
        {
            _segments[i].Background = new SolidColorBrush(ColorHelper.FromArgb(Alphas[i], c.R, c.G, c.B));
            _segments[i].BorderBrush = new SolidColorBrush(c);
        }
    }

    private void Update()
    {
        if (Value is not MemorySample m) return;
        ulong[] sizes = [m.InUseSegment, m.Modified, m.Standby, m.Free];
        string[] notes =
        [
            "Memory used by processes, drivers or the operating system",
            "Memory whose contents must be written to disk before it can be used for another purpose",
            "Memory that contains cached data and code that is not actively in use",
            "Memory that is not currently in use, and that will be repurposed first",
        ];
        for (int i = 0; i < 4; i++)
        {
            _grid.ColumnDefinitions[i].Width = new GridLength(sizes[i], GridUnitType.Star);
            ToolTipService.SetToolTip(_segments[i], $"{Names[i]} ({Format.Bytes(sizes[i])})\n{notes[i]}");
        }
    }
}
