using Detective.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Detective.Views;

/// <summary>Shared by every disk item; <see cref="SetVm"/> swaps which one is shown.</summary>
public sealed partial class DiskView : UserControl
{
    public DiskView()
    {
        InitializeComponent();
    }

    public DiskVm? Vm { get; private set; }

    public void SetVm(DiskVm vm)
    {
        if (ReferenceEquals(Vm, vm)) return;
        Vm = vm;
        Bindings.Update();
    }
}
