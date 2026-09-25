using System.Collections;
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

    public static readonly DependencyProperty LiveStatsProperty = DependencyProperty.Register(
        nameof(LiveStats), typeof(object), typeof(DetailsPanel),
        new PropertyMetadata(null, (d, e) => ((DetailsPanel)d).LiveRepeater.ItemsSource = e.NewValue));

    public object? LiveStats
    {
        get => GetValue(LiveStatsProperty);
        set => SetValue(LiveStatsProperty, value);
    }

    public static readonly DependencyProperty StaticInfoProperty = DependencyProperty.Register(
        nameof(StaticInfo), typeof(object), typeof(DetailsPanel),
        new PropertyMetadata(null, (d, e) => ((DetailsPanel)d).StaticRepeater.ItemsSource = e.NewValue));

    public object? StaticInfo
    {
        get => GetValue(StaticInfoProperty);
        set => SetValue(StaticInfoProperty, value);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (var source in new[] { LiveStats, StaticInfo })
            if (source is IEnumerable items)
                foreach (var stat in items.OfType<Stat>())
                    text.AppendLine($"{stat.Label}\t{stat.Value}");

        var package = new DataPackage();
        package.SetText(text.ToString());
        Clipboard.SetContent(package);
    }
}
