using System.Diagnostics;
using Detective.Core;
using Detective.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Detective.Views;

public sealed partial class ProcessesView : UserControl
{
    private static readonly string SortDown = ((char)0xE70D).ToString(), SortUp = ((char)0xE70E).ToString();
    private readonly MenuFlyout _rowMenu;
    private bool _restoringSelection;

    public ProcessesView(ProcessesVm vm)
    {
        Vm = vm;
        InitializeComponent();
        _rowMenu = (MenuFlyout)Resources["RowMenu"];

        // Set in code rather than x:Bind so the Native AOT marshalling generator sees the collection type.
        List.ItemsSource = Vm.Rows;
        Vm.RowsUpdated += (_, _) => RestoreSelection();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProcessesVm.SortColumn) or nameof(ProcessesVm.SortDescending)) ShowSort();
        };
        ShowSort();

        foreach (var (splitter, column) in new[]
        {
            (PidSplitter, ProcessColumn.Pid),
            (CpuSplitter, ProcessColumn.Cpu),
            (MemorySplitter, ProcessColumn.Memory),
            (DiskSplitter, ProcessColumn.Disk),
            (NetworkSplitter, ProcessColumn.Network),
            (GpuSplitter, ProcessColumn.Gpu),
        })
        {
            splitter.GetWidth = () => ProcessColumns.Current.Get(column);
            splitter.SetWidth = w => ProcessColumns.Current.Set(column, w);
            splitter.Committed += (_, _) => Vm.SaveColumnWidths();
        }
    }

    public ProcessesVm Vm { get; }

    /// <summary>Asks the window to restart Detective elevated.</summary>
    public event EventHandler? RunAsAdminRequested;

    public Visibility ShowElevateLink(bool networkAvailable) =>
        networkAvailable || ProcessesVm.IsElevated ? Visibility.Collapsed : Visibility.Visible;

    private void ShowSort()
    {
        foreach (var (icon, column) in new[]
        {
            (NameSort, ProcessColumn.Name),
            (PidSort, ProcessColumn.Pid),
            (CpuSort, ProcessColumn.Cpu),
            (MemorySort, ProcessColumn.Memory),
            (DiskSort, ProcessColumn.Disk),
            (NetworkSort, ProcessColumn.Network),
            (GpuSort, ProcessColumn.Gpu),
        })
        {
            bool active = Vm.SortColumn == column;
            icon.Glyph = active ? (Vm.SortDescending ? SortDown : SortUp) : "";
            icon.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<ProcessColumn>(tag, out var column))
            Vm.SortBy(column);
    }

    private void Expander_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProcessRowVm row }) Vm.Toggle(row);
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Reordering rows briefly drops the ListView's selection; the view model keeps the real one.
        if (_restoringSelection) return;
        if (List.SelectedItem is ProcessRowVm row) Vm.Selected = row;
        else if (e.RemovedItems.Count > 0 && Vm.Rows.Contains(Vm.Selected!)) RestoreSelection();
        else Vm.Selected = null;
    }

    private void RestoreSelection()
    {
        if (ReferenceEquals(List.SelectedItem, Vm.Selected)) return;
        _restoringSelection = true;
        try { List.SelectedItem = Vm.Selected is { } s && Vm.Rows.Contains(s) ? s : null; }
        finally { _restoringSelection = false; }
    }

    /// <summary>Left collapses (or moves to the parent), Right expands, as in a tree view.</summary>
    private void List_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Vm.Selected is not { } row) return;
        if (e.Key == VirtualKey.Right && (row.HasChildren || row.IsGroup) && !row.IsExpanded)
        {
            Vm.SetExpanded(row, true);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Left)
        {
            if ((row.HasChildren || row.IsGroup) && row.IsExpanded)
                Vm.SetExpanded(row, false);
            else if (ParentRow(row) is { } parent)
            {
                Vm.Selected = parent;
                RestoreSelection();
                (List.ContainerFromItem(parent) as Control)?.Focus(FocusState.Keyboard);
            }
            e.Handled = true;
        }
    }

    /// <summary>The nearest row above with a smaller indent.</summary>
    private ProcessRowVm? ParentRow(ProcessRowVm row)
    {
        int i = Vm.Rows.IndexOf(row);
        for (int j = i - 1; j >= 0; j--)
            if (Vm.Rows[j].IsGroup || Vm.Rows[j].Indent.Left < row.Indent.Left)
                return Vm.Rows[j];
        return null;
    }

    private void List_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        // Find the row under the pointer (or the focused row for the menu key / Shift+F10).
        DependencyObject? node = args.OriginalSource as DependencyObject;
        while (node is not null and not ListViewItem) node = VisualTreeHelper.GetParent(node);
        if (node is not ListViewItem container || List.ItemFromContainer(container) is not ProcessRowVm row || row.IsGroup) return;

        Vm.Selected = row;
        RestoreSelection();
        bool canEnd = Vm.CanEnd;
        ((MenuFlyoutItem)_rowMenu.Items[0]).IsEnabled = canEnd;
        ((MenuFlyoutItem)_rowMenu.Items[1]).IsEnabled = canEnd;
        ((MenuFlyoutItem)_rowMenu.Items[3]).IsEnabled = row.Sample?.Path is not null;
        if (args.TryGetPosition(container, out var point))
            _rowMenu.ShowAt(container, point);
        else
            _rowMenu.ShowAt(container);
        args.Handled = true;
    }

    private void EndTask_Click(object sender, RoutedEventArgs e) => _ = EndAsync(tree: false);

    private void EndTree_Click(object sender, RoutedEventArgs e) => _ = EndAsync(tree: true);

    private void EndTask_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = EndAsync(tree: false);
    }

    private void EndTree_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = EndAsync(tree: true);
    }

    private bool _ending;

    private async Task EndAsync(bool tree)
    {
        if (_ending || Vm.Selected?.Sample is not { } target) return;
        if (ProcessKiller.Forbidden(target.Pid) is { } reason)
        {
            Show(InfoBarSeverity.Warning, reason);
            return;
        }

        _ending = true;
        try
        {
            string name = $"{target.DisplayName} (PID {target.Pid})";
            int count = tree ? (await Task.Run(() => ProcessKiller.Tree(target.Key))).Count : 1;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = tree ? "End process tree?" : "End process?",
                Content = tree && count > 1
                    ? $"This ends {name} and the {count - 1} processes it started. Unsaved data in them will be lost."
                    : $"This ends {name}. Unsaved data in it will be lost.",
                PrimaryButtonText = tree ? "End process tree" : "End process",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var result = await Task.Run(() => tree ? ProcessKiller.KillTree(target.Key) : ProcessKiller.Kill(target.Key));
            Report(result, name);
        }
        finally
        {
            _ending = false;
        }
    }

    private void Report(KillResult result, string name)
    {
        if (result.Failures.Count == 0)
        {
            Message.IsOpen = false;
            return;
        }
        if (result.Failures.All(f => f.Outcome == KillOutcome.AlreadyExited))
        {
            Show(InfoBarSeverity.Informational, $"{name} had already exited.");
            return;
        }

        bool denied = result.Failures.Any(f => f.Outcome == KillOutcome.AccessDenied);
        bool critical = result.Failures.Any(f => f.Outcome == KillOutcome.Protected);
        string what = result.Requested == 1
            ? $"Couldn't end {name}."
            : $"Ended {result.Ended} of {result.Requested} processes; {result.Failures.Count} couldn't be ended.";
        string why = critical
            ? " Windows needs some of these processes to keep running, so Detective won't end them."
            : denied
                ? ProcessesVm.IsElevated
                    ? " Access was denied: the process is protected by Windows."
                    : " Access was denied. Processes run by Windows or another user can only be ended as administrator."
                : "";
        Show(InfoBarSeverity.Error, what + why);
    }

    private void Show(InfoBarSeverity severity, string text)
    {
        Message.Severity = severity;
        Message.Message = text;
        Message.IsOpen = true;
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Selected?.Sample?.Path is not { } path) return;
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (System.ComponentModel.Win32Exception ex) { Show(InfoBarSeverity.Error, ex.Message); }
    }

    private void RunAsAdmin_Click(object sender, RoutedEventArgs e) => RunAsAdminRequested?.Invoke(this, EventArgs.Empty);
}
