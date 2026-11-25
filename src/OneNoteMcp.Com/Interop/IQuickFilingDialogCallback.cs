using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OneNoteMcp.Com.Interop;

/// <summary>
/// Hand-declared OneNote 15 <c>IQuickFilingDialogCallback</c> (dual): slot 7 <c>OnDialogClosed</c> (story 4.9; spike 4.13).
/// <para>
/// <b>Public, the one exception to AD-2's internal interop types</b> (Phill, 2026-11-23, 4.9 plan OQ2 a): the runtime exposes
/// a managed object to COM only through public interfaces, and spike 4.13's unit test found the callback's CCW answering
/// <c>E_NOINTERFACE</c> while its interface was internal. Its dialog parameter is typed <see cref="object"/> so the dialog
/// interface (<see cref="IQuickFilingDialog"/>) stays internal; <see cref="QuickFilingCallback"/> casts it inside. Nothing
/// outside <c>OneNoteMcp.Com</c> implements or calls it.
/// </para>
/// </summary>
[ComImport]
[Guid("627EA7B4-95B5-4980-84C1-9D20DA4460B1")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IQuickFilingDialogCallback
{
    // slot 7: OnDialogClosed(IQuickFilingDialog*). The pointer is an IQuickFilingDialog, itself an IDispatch.
    void OnDialogClosed([MarshalAs(UnmanagedType.IDispatch)] object dialog);
}
