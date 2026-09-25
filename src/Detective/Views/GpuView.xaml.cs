using Detective.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Detective.Views;

/// <summary>Shared by every GPU item; <see cref="SetVm"/> swaps which one is shown.</summary>
public sealed partial class GpuView : UserControl
{
    public GpuView()
    {
        InitializeComponent();
    }

    public GpuVm? Vm { get; private set; }

    public void SetVm(GpuVm vm)
    {
        if (ReferenceEquals(Vm, vm)) return;
        Vm = vm;
        Bindings.Update();
    }
}
