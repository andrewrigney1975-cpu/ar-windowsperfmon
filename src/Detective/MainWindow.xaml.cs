using Detective.Core;
using Detective.ViewModels;
using Detective.Views;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Graphics;

namespace Detective;

public sealed partial class MainWindow : Window
{
    private const double StripMaxHeight = 300;
    private const double StripMaxFraction = 0.30;
    private const int StripRows = 2;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Sampler _sampler;
    private readonly CpuView _cpuView;
    private readonly MemoryView _memoryView;
    private readonly DiskView _diskView = new();
    private readonly NetworkView _networkView = new();
    private readonly GpuView _gpuView = new();

    public MainWindow()
    {
        if (SnapshotMode.Directory is not null) _settings.Persist = false;
        Shell = new ShellVm(_settings);
        InitializeComponent();
        // Set in code rather than x:Bind so the Native AOT marshalling generator sees the collection type.
        StripRepeater.ItemsSource = Shell.Items;
        // WinUI gives the first control (the ☰ button) programmatic focus at startup, which draws a focus
        // rectangle. Pointer focus keeps the same focus target without the rectangle until the keyboard is used.
        RootGrid.Loaded += (_, _) => Nav.Focus(FocusState.Pointer);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Detective.ico"));
        // Settings hold the size in DIPs so the window opens at the same visual size on any display scale.
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var (width, height) = SnapshotMode.Size ?? (_settings.Width, _settings.Height);
        AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
        FitToWorkArea();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 760;
            presenter.PreferredMinimumHeight = 560;
        }

        _cpuView = new CpuView(Shell.Cpu);
        _memoryView = new MemoryView(Shell.Memory);
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellVm.Selected)) ShowSelected();
        };
        ShowSelected();

        SpeedRadios.SelectedIndex = Math.Clamp(_settings.SpeedIndex, 0, 3);
        ThemeRadios.SelectedIndex = Math.Clamp(_settings.ThemeIndex, 0, 2);
        ApplyTheme();
        ChartWindow.Current.SetInterval(_settings.Interval);

        _sampler = new Sampler(_settings.Interval) { Paused = _settings.Paused };
        var dispatcher = DispatcherQueue;
        _sampler.Sampled += snapshot => dispatcher.TryEnqueue(() => OnSnapshot(snapshot));
        _sampler.Start();

        Closed += OnClosed;

        if (SnapshotMode.Directory is not null)
        {
            if (SnapshotMode.Theme is "light" or "dark")
                RootGrid.RequestedTheme = SnapshotMode.Theme == "light" ? ElementTheme.Light : ElementTheme.Dark;
            SnapshotMode.PrepareBackground(RootGrid);
            _ = SnapshotMode.RunAsync(this, RootGrid, Shell, logical => Shell.Cpu.ShowLogical = logical);
        }
    }

    public ShellVm Shell { get; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void OnSnapshot(Snapshot snapshot)
    {
        Shell.Apply(snapshot);
    }

    private void ShowSelected()
    {
        var item = Shell.Selected;
        FocusHost.Content = item switch
        {
            CpuVm => _cpuView,
            MemoryVm => _memoryView,
            DiskVm d => Swap(_diskView, () => _diskView.SetVm(d)),
            NetworkVm n => Swap(_networkView, () => _networkView.SetVm(n)),
            GpuVm g => Swap(_gpuView, () => _gpuView.SetVm(g)),
            _ => null,
        };

        if (item is not null)
        {
            string tag = item.Kind.ToString();
            Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        }
    }

    private static UIElement Swap(UIElement view, Action setVm)
    {
        setVm();
        return view;
    }

    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer == SettingsNavItem)
        {
            FlyoutBase.ShowAttachedFlyout(SettingsNavItem);
            return;
        }
        if (args.InvokedItemContainer?.Tag is string tag && Enum.TryParse<FocusKind>(tag, out var kind))
        {
            Shell.SelectKind(kind);
            // A kind with no items yet (e.g. no GPU found) keeps the current focus; re-sync the rail to it.
            ShowSelected();
        }
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        // The item is bound to Tag explicitly: DataContext isn't populated for x:Bind templates here.
        if (sender is FrameworkElement { Tag: FocusItemVm item })
            Shell.Selected = item;
    }

    /// <summary>
    /// The strip takes at most 300 px and at most 30% of the pane, so the focus area keeps ≥ 70%.
    /// Tiles sit in two rows; each tile's chart is kept a little wider than tall.
    /// </summary>
    private void ContentRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double strip = Math.Floor(Math.Min(StripMaxHeight, e.NewSize.Height * StripMaxFraction));
        StripRow.Height = new GridLength(strip);

        const double verticalPadding = 12 + 1, rowSpacing = 6, labels = 34, tilePadding = 10;
        double tileHeight = Math.Max(60, Math.Floor((strip - verticalPadding - rowSpacing) / StripRows));
        double chartHeight = Math.Max(24, tileHeight - labels - tilePadding);
        double tileWidth = Math.Floor(Math.Clamp(chartHeight * 1.6 + tilePadding, 110, 220));

        ChartWindow.Current.TileHeight = tileHeight;
        ChartWindow.Current.TileWidth = tileWidth;
        StripLayout.MinItemHeight = tileHeight;
        StripLayout.MinItemWidth = tileWidth;
    }

    /// <summary>Keeps the whole window (and so the bottom tile row) on screen, shrinking and moving it if needed.</summary>
    private void FitToWorkArea()
    {
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min(AppWindow.Size.Width, work.Width);
        int height = Math.Min(AppWindow.Size.Height, work.Height);
        int x = Math.Clamp(AppWindow.Position.X, work.X, work.X + work.Width - width);
        int y = Math.Clamp(AppWindow.Position.Y, work.Y, work.Y + work.Height - height);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void FocusHost_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ChartWindow.Current.DetailsMaxHeight = Math.Max(120, Math.Floor(e.NewSize.Height * 0.32));

    private void SpeedRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedRadios.SelectedIndex < 0) return;
        _settings.SpeedIndex = SpeedRadios.SelectedIndex;
        if (_sampler is null) return;
        _sampler.Paused = _settings.Paused;
        if (!_settings.Paused)
        {
            _sampler.Interval = _settings.Interval;
            ChartWindow.Current.SetInterval(_settings.Interval);
        }
        _settings.Save();
    }

    private void ThemeRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeRadios.SelectedIndex < 0) return;
        _settings.ThemeIndex = ThemeRadios.SelectedIndex;
        ApplyTheme();
        _settings.Save();
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _settings.ThemeIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        UpdateCaptionButtons();
        RootGrid.ActualThemeChanged -= OnActualThemeChanged;
        RootGrid.ActualThemeChanged += OnActualThemeChanged;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => UpdateCaptionButtons();

    /// <summary>Caption buttons don't follow an app-level theme override on their own.</summary>
    private void UpdateCaptionButtons()
    {
        var bar = AppWindow.TitleBar;
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonHoverBackgroundColor = dark ? ColorHelper.FromArgb(0x20, 255, 255, 255) : ColorHelper.FromArgb(0x20, 0, 0, 0);
        bar.ButtonInactiveForegroundColor = Colors.Gray;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (SnapshotMode.Directory is not null)
        {
            _sampler.Dispose();
            return;
        }
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            _settings.Width = (int)(AppWindow.Size.Width / scale);
            _settings.Height = (int)(AppWindow.Size.Height / scale);
        }
        _settings.Save();
        _sampler.Dispose();
    }
}
