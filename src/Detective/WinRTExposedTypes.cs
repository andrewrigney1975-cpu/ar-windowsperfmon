using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using WinRT;

namespace Detective.ViewModels;

// Native AOT: collections handed to XAML as ItemsSource must be marshalled as IBindableVector /
// INotifyCollectionChanged. CsWinRT's AOT generator doesn't emit that marshalling for collection types (not
// even for concrete subclasses defined here), so XAML rejects them ("not a supported vector") and the app
// fails to start. These classes declare the WinRT interfaces explicitly, in the same form the generator
// uses for the types it does handle.

/// <summary>The summary strip's items.</summary>
[WinRTRuntimeClassName("Microsoft.UI.Xaml.Interop.IBindableVector")]
[WinRTExposedType(typeof(BindableCollectionDetails))]
public sealed class FocusItemCollection : ObservableCollection<FocusItemVm>;

/// <summary>A details block's label/value rows.</summary>
[WinRTRuntimeClassName("Microsoft.UI.Xaml.Interop.IBindableVector")]
[WinRTExposedType(typeof(BindableCollectionDetails))]
public sealed class StatCollection : ObservableCollection<Stat>;

/// <summary>GPU engine names offered in the chart pickers.</summary>
[WinRTRuntimeClassName("Microsoft.UI.Xaml.Interop.IBindableVector")]
[WinRTExposedType(typeof(BindableCollectionDetails))]
public sealed class NameCollection : ObservableCollection<string>;

/// <summary>
/// WinRT view of an ObservableCollection: IBindableVector (via IList), IBindableIterable (via IEnumerable),
/// INotifyCollectionChanged and INotifyPropertyChanged.
/// </summary>
internal sealed class BindableCollectionDetails : IWinRTExposedTypeDetails
{
    public ComWrappers.ComInterfaceEntry[] GetExposedInterfaces() =>
    [
        new()
        {
            IID = ABI.System.Collections.IListMethods.IID,
            Vtable = ABI.System.Collections.IListMethods.AbiToProjectionVftablePtr,
        },
        new()
        {
            IID = ABI.System.Collections.IEnumerableMethods.IID,
            Vtable = ABI.System.Collections.IEnumerableMethods.AbiToProjectionVftablePtr,
        },
        new()
        {
            IID = ABI.System.Collections.Specialized.INotifyCollectionChangedMethods.IID,
            Vtable = ABI.System.Collections.Specialized.INotifyCollectionChangedMethods.AbiToProjectionVftablePtr,
        },
        new()
        {
            IID = ABI.System.ComponentModel.INotifyPropertyChangedMethods.IID,
            Vtable = ABI.System.ComponentModel.INotifyPropertyChangedMethods.AbiToProjectionVftablePtr,
        },
    ];
}
