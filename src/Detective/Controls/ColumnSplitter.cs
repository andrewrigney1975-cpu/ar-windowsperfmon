using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Detective.Controls;

/// <summary>
/// A grip on the left edge of a table column's header. Dragging it left widens the column (the Name column
/// to its left takes up the difference), matching how the right-aligned numeric columns read.
/// </summary>
public sealed partial class ColumnSplitter : Grid
{
    private double _startX, _startWidth;
    private bool _dragging;

    public ColumnSplitter()
    {
        Width = 8;
        HorizontalAlignment = HorizontalAlignment.Left;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => End();
    }

    /// <summary>Reads the column's current width when a drag starts.</summary>
    public Func<double>? GetWidth { get; set; }

    /// <summary>Applies a new width while dragging.</summary>
    public Action<double>? SetWidth { get; set; }

    /// <summary>Raised once when a drag ends, to persist the width.</summary>
    public event EventHandler? Committed;

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (GetWidth is null || !CapturePointer(e.Pointer)) return;
        _dragging = true;
        _startX = e.GetCurrentPoint(null).Position.X;
        _startWidth = GetWidth();
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        SetWidth?.Invoke(_startWidth + _startX - e.GetCurrentPoint(null).Position.X);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        End();
        e.Handled = true;
    }

    private void End()
    {
        if (!_dragging) return;
        _dragging = false;
        Committed?.Invoke(this, EventArgs.Empty);
    }
}
