using Detective.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Detective.Views;

public sealed partial class MemoryView : UserControl
{
    public MemoryView(MemoryVm vm)
    {
        Vm = vm;
        InitializeComponent();
    }

    public MemoryVm Vm { get; }
}
