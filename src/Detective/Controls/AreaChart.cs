using Detective.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Detective.Controls;

/// <summary>Task Manager lightens its accents on dark backgrounds so thin lines stay legible.</summary>
public static class ThemeAccent
{
    public static Color For(Color accent, FrameworkElement element) =>
        element.ActualTheme == ElementTheme.Dark ? Mix(accent, 0.4) : accent;

    private static Color Mix(Color c, double towardWhite) => ColorHelper.FromArgb(
        c.A,
        (byte)(c.R + (255 - c.R) * towardWhite),
        (byte)(c.G + (255 - c.G) * towardWhite),
        (byte)(c.B + (255 - c.B) * towardWhite));
}

/// <summary>Global sample clock. Charts redraw on each tick and scroll their grid with it.</summary>
public static class ChartClock
{
    public static long Tick { get; private set; }

    public static event EventHandler? Ticked;

    public static void Advance()
    {
        Tick++;
        Ticked?.Invoke(null, EventArgs.Empty);
    }
}

/// <summary>
/// Task Manager-style filled area chart: accent border, scrolling light grid, translucent fill under a
/// 1 px line, newest sample on the right edge. An optional secondary series is drawn as a dashed line.
/// Only charts that are loaded (in the visual tree) redraw.
/// </summary>
public sealed partial class AreaChart : UserControl
{
    private const int GridRows = 10;
    private const int SamplesPerGridColumn = 5;

    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Microsoft.UI.Xaml.Shapes.Path _grid = new() { StrokeThickness = 1 };
    private readonly Polygon _fill = new();
    private readonly Polyline _line = new() { StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round };
    private readonly Polyline _secondaryLine = new()
    {
        StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 4, 2 },
        StrokeLineJoin = PenLineJoin.Round,
    };
    private readonly Border _border = new() { BorderThickness = new Thickness(1) };
    private bool _subscribed;

    public AreaChart()
    {
        _canvas.Children.Add(_grid);
        _canvas.Children.Add(_fill);
        _canvas.Children.Add(_secondaryLine);
        _canvas.Children.Add(_line);
        _border.Child = _canvas;
        Content = _border;

        _canvas.SizeChanged += (_, e) =>
        {
            _canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            Redraw();
        };
        Loaded += (_, _) =>
        {
            if (!_subscribed) { ChartClock.Ticked += OnTick; _subscribed = true; }
            Redraw();
        };
        Unloaded += (_, _) =>
        {
            if (_subscribed) { ChartClock.Ticked -= OnTick; _subscribed = false; }
        };
        ActualThemeChanged += (_, _) => ApplyAccent();
        ApplyAccent();
    }

    // Plain CLR properties rather than dependency properties: x:Bind can set them directly, and keeping the
    // ring buffers on the .NET side avoids marshalling them as WinRT objects (which Native AOT can't do for
    // types outside this assembly).
    private RingBuffer? _primary;
    private RingBuffer? _secondary;

    /// <summary>The filled series.</summary>
    public RingBuffer? Primary
    {
        get => _primary;
        set
        {
            _primary = value;
            Redraw();
        }
    }

    /// <summary>Optional dashed series, e.g. disk writes or network send.</summary>
    public RingBuffer? Secondary
    {
        get => _secondary;
        set
        {
            _secondary = value;
            Redraw();
        }
    }

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(AreaChart), new PropertyMetadata(100.0, OnDataChanged));

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(AreaChart), new PropertyMetadata(ColorHelper.FromArgb(255, 17, 125, 187), OnAccentChanged));

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(AreaChart), new PropertyMetadata(true, OnDataChanged));

    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AreaChart)d).Redraw();

    private static void OnAccentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (AreaChart)d;
        chart.ApplyAccent();
        chart.Redraw();
    }

    private void OnTick(object? sender, EventArgs e) => Redraw();

    private void ApplyAccent()
    {
        var c = ThemeAccent.For(Accent, this);
        bool dark = ActualTheme == ElementTheme.Dark;
        var solid = new SolidColorBrush(c);
        _border.BorderBrush = solid;
        _line.Stroke = solid;
        _secondaryLine.Stroke = solid;
        _fill.Fill = new SolidColorBrush(ColorHelper.FromArgb(dark ? (byte)0x48 : (byte)0x38, c.R, c.G, c.B));
        _grid.Stroke = new SolidColorBrush(ColorHelper.FromArgb(dark ? (byte)0x30 : (byte)0x28, c.R, c.G, c.B));
    }

    private void Redraw()
    {
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        if (w < 2 || h < 2) return;

        var primary = Primary;
        int capacity = primary?.Capacity ?? RingBuffer.DefaultCapacity;
        double dx = w / (capacity - 1);
        double max = Maximum > 0 ? Maximum : 100;

        _grid.Data = ShowGrid ? BuildGrid(w, h, dx) : null;

        if (primary is { Count: > 0 })
        {
            _fill.Points = BuildPoints(primary, w, h, dx, max, closed: true);
            _line.Points = BuildPoints(primary, w, h, dx, max, closed: false);
        }
        else
        {
            _fill.Points = new PointCollection();
            _line.Points = new PointCollection();
        }

        _secondaryLine.Points = Secondary is { Count: > 0 } secondary
            ? BuildPoints(secondary, w, h, dx, max, closed: false)
            : new PointCollection();
    }

    private static PointCollection BuildPoints(RingBuffer data, double w, double h, double dx, double max, bool closed)
    {
        var points = new PointCollection();
        int n = data.Count;
        double x0 = w - (n - 1) * dx;
        double baseline = h - 0.5;
        double range = h - 1;

        if (closed) points.Add(new Point(x0, h));
        for (int i = 0; i < n; i++)
        {
            double frac = Math.Clamp(data[i] / max, 0, 1);
            points.Add(new Point(x0 + i * dx, baseline - frac * range));
        }
        if (closed) points.Add(new Point(w, h));
        return points;
    }

    private static GeometryGroup BuildGrid(double w, double h, double dx)
    {
        var group = new GeometryGroup();
        for (int r = 1; r < GridRows; r++)
        {
            double y = Math.Round(h * r / GridRows) + 0.5;
            group.Children.Add(new LineGeometry { StartPoint = new Point(0, y), EndPoint = new Point(w, y) });
        }

        // Vertical lines move one sample left per tick, so the grid scrolls with the data.
        int phase = (int)(ChartClock.Tick % SamplesPerGridColumn);
        for (double x = w - phase * dx; x > 0; x -= SamplesPerGridColumn * dx)
        {
            double px = Math.Round(x) + 0.5;
            group.Children.Add(new LineGeometry { StartPoint = new Point(px, 0), EndPoint = new Point(px, h) });
        }
        return group;
    }
}
