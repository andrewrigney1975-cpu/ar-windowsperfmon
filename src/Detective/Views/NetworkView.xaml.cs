using Detective.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Detective.Views;

/// <summary>Shared by every network adapter item; <see cref="SetVm"/> swaps which one is shown.</summary>
public sealed partial class NetworkView : UserControl
{
    public NetworkView()
    {
        InitializeComponent();
    }

    public NetworkVm? Vm { get; private set; }

    public void SetVm(NetworkVm vm)
    {
        if (ReferenceEquals(Vm, vm)) return;
        Vm = vm;
        Bindings.Update();
    }
}
