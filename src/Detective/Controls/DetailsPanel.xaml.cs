using System.Text;
using Detective.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Detective.Controls;

public sealed partial class DetailsPanel : UserControl
{
    public DetailsPanel()
    {
        InitializeComponent();
    }

    /// <summary>The right-click menu, so a view can add its own commands next to Copy.</summary>
    public MenuFlyout Menu => (MenuFlyout)ContextFlyout;

    private StatCollection? _liveStats;
    private StatCollection? _staticInfo;

    /// <summary>Big live figures (left column). Plain property so x:Bind sets it without WinRT boxing.</summary>
    public StatCollection? LiveStats
    {
        get => _liveStats;
        set => LiveRepeater.ItemsSource = _liveStats = value;
    }

    /// <summary>Static key/value facts (right column).</summary>
    public StatCollection? StaticInfo
    {
        get => _staticInfo;
        set => StaticRepeater.ItemsSource = _staticInfo = value;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (var source in new[] { LiveStats, StaticInfo })
            foreach (var stat in source ?? [])
                text.AppendLine($"{stat.Label}\t{stat.Value}");

        var package = new DataPackage();
        package.SetText(text.ToString());
        Clipboard.SetContent(package);
    }
}
