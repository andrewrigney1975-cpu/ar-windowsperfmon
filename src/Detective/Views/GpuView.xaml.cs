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
        // Collections are handed to WinRT in code-behind, not x:Bind: under Native AOT the marshalling generator
        // only sees assignments in C# source, and XAML-generated code isn't analysed.
        ComboBox[] combos = [Engine0, Engine1, Engine2, Engine3];
        for (int i = 0; i < combos.Length; i++) combos[i].ItemsSource = vm.Slots[i].Options;
        Bindings.Update();
    }
}
