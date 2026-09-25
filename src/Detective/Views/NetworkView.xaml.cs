using Detective.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Detective.Views;

/// <summary>Shared by every network adapter item; <see cref="SetVm"/> swaps which one is shown.</summary>
public sealed partial class NetworkView : UserControl
{
    public NetworkView()
    {
        InitializeComponent();

        var reset = new MenuFlyoutItem { Text = "Reset maximum speeds", Icon = new FontIcon { Glyph = "\uE72C" } };
        reset.Click += ResetPeaks_Click;
        Details.Menu.Items.Add(new MenuFlyoutSeparator());
        Details.Menu.Items.Add(reset);
    }

    private void ResetPeaks_Click(object sender, RoutedEventArgs e) => Vm?.ResetPeaks();

    public NetworkVm? Vm { get; private set; }

    public void SetVm(NetworkVm vm)
    {
        if (ReferenceEquals(Vm, vm)) return;
        Vm = vm;
        Bindings.Update();
    }
}
