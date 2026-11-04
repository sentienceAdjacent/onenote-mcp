using System.Runtime.InteropServices;

namespace OneNoteMcp.Com.Interop;

/// <summary>
/// The OneNote <c>Windows</c> collection (dual), declared only as far as the members needed.
/// Slots 7-10 in the type library: Item, Count, _NewEnum, CurrentWindow.
/// Obtain it by casting <see cref="IApplication.Windows"/>.
/// </summary>
[ComImport]
[Guid("6D4B9C3E-CC05-493F-85E2-43D1006DF96A")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IWindows
{
    // slot 7: Item(index) returns Window; kept as IDispatch until a story needs it.
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object GetItem(uint index);

    // slot 8
    uint Count { get; }
}
