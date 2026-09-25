using Detective.Controls;
using Detective.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Detective.Views;

public sealed partial class CpuView : UserControl
{
    public CpuView(CpuVm vm)
    {
        Vm = vm;
        InitializeComponent();
        vm.LogicalChanged += (_, _) => BuildLogicalGrid();
        vm.LogicalAccentChanged += (_, i) =>
        {
            if (i < LogicalGrid.Children.Count) ((AreaChart)LogicalGrid.Children[i]).Accent = Vm.LogicalAccents[i];
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CpuVm.ShowLogical)) SyncMode();
        };
        BuildLogicalGrid();
        SyncMode();
    }

    public CpuVm Vm { get; }

    public string Caption(bool logical) =>
        logical ? "% Utilization of each logical processor" : "% Utilization";

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Vm.ShowLogical = ModeCombo.SelectedIndex == 1;
        SyncMode();
    }

    private void OverallMenu_Click(object sender, RoutedEventArgs e) => SetMode(false);

    private void LogicalMenu_Click(object sender, RoutedEventArgs e) => SetMode(true);

    private void SetMode(bool logical)
    {
        Vm.ShowLogical = logical;
        SyncMode();
    }

    private void SyncMode()
    {
        int index = Vm.ShowLogical ? 1 : 0;
        if (ModeCombo.SelectedIndex != index) ModeCombo.SelectedIndex = index;
        OverallMenuItem.IsChecked = !Vm.ShowLogical;
        LogicalMenuItem.IsChecked = Vm.ShowLogical;
    }

    private void BuildLogicalGrid()
    {
        LogicalGrid.Children.Clear();
        for (int i = 0; i < Vm.Logical.Count; i++)
        {
            var chart = new AreaChart { Primary = Vm.Logical[i], Accent = Vm.LogicalAccents[i] };
            ToolTipService.SetToolTip(chart, $"Logical processor {i}");
            LogicalGrid.Children.Add(chart);
        }
        LayoutLogicalGrid();
    }

    private void LogicalGrid_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutLogicalGrid();

    /// <summary>Chooses the column count whose cells come closest to Task Manager's wide cell shape.</summary>
    private void LayoutLogicalGrid()
    {
        int n = LogicalGrid.Children.Count;
        double w = LogicalGrid.ActualWidth, h = LogicalGrid.ActualHeight;
        if (n == 0 || w <= 0 || h <= 0) return;

        const double targetAspect = 1.6;
        int bestCols = 1;
        double bestScore = double.MaxValue;
        for (int cols = 1; cols <= n; cols++)
        {
            int rows = (n + cols - 1) / cols;
            double score = Math.Abs(Math.Log((w / cols) / (h / rows) / targetAspect));
            if (score < bestScore)
            {
                bestScore = score;
                bestCols = cols;
            }
        }
        int bestRows = (n + bestCols - 1) / bestCols;

        if (LogicalGrid.ColumnDefinitions.Count != bestCols || LogicalGrid.RowDefinitions.Count != bestRows)
        {
            LogicalGrid.ColumnDefinitions.Clear();
            LogicalGrid.RowDefinitions.Clear();
            for (int c = 0; c < bestCols; c++) LogicalGrid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int r = 0; r < bestRows; r++) LogicalGrid.RowDefinitions.Add(new RowDefinition());
        }
        for (int i = 0; i < n; i++)
        {
            var child = (FrameworkElement)LogicalGrid.Children[i];
            Grid.SetRow(child, i / bestCols);
            Grid.SetColumn(child, i % bestCols);
        }
    }
}
