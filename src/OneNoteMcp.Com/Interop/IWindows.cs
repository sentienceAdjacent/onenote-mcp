using System.Runtime.InteropServices;

namespace OneNoteMcp.Com.Interop;

/// <summary>
/// The OneNote <c>Windows</c> collection (dual). Slots 7-10 in the type library (spike 4.12, read 2026-11-16): Item, Count,
/// _NewEnum, CurrentWindow. Obtain it by casting <see cref="IApplication.Windows"/>. Walk it with
/// <see cref="GetItem"/> from 0 to <see cref="Count"/> - 1: <c>Item(Count)</c> is <c>E_INVALIDARG</c> (W1). With no window
/// open, <see cref="Count"/> is 0 and <see cref="CurrentWindow"/> null (W6). Every wrapper obtained from it is released
/// with <see cref="Marshal.ReleaseComObject"/> on the STA thread before the call returns (OneMore, research row 10).
/// </summary>
[ComImport]
[Guid("6D4B9C3E-CC05-493F-85E2-43D1006DF96A")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IWindows
{
    // slot 7: propget Item(Index) returns Window*
    [return: MarshalAs(UnmanagedType.Interface)]
    IWindow GetItem(uint index);

    // slot 8
    uint Count { get; }

    // slot 9: propget _NewEnum returns IUnknown*. Not used: the walk is Item(0) … Item(Count - 1).
    [Obsolete("Placeholder for the _NewEnum slot; walk the collection with GetItem.", error: true)]
    void NewEnumPlaceholder();

    // slot 10: propget CurrentWindow returns Window*; null with no window open (spike 4.12 W6)
    IWindow? CurrentWindow { [return: MarshalAs(UnmanagedType.Interface)] get; }
}
