using System.Runtime.InteropServices;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Spike 4.12: the spike's own early-bound copy of OneNote's <c>Windows</c> collection (dual), internal to the test
/// assembly. <c>src/OneNoteMcp.Com/Interop/IWindows.cs</c> declares only slots 7–8 and is internal to Com (AD-2), so it
/// is left alone. Slot order is the type library's (comtypes module generated on this machine, read 2026-11-16): 7
/// <c>Item(uint)</c>, 8 <c>Count</c>, 9 <c>_NewEnum</c>, 10 <c>CurrentWindow</c>. Every wrapper obtained from it is
/// released with <see cref="Marshal.ReleaseComObject"/> in a <c>finally</c> before the STA delegate returns (OneMore,
/// research row 10).
/// </summary>
[ComImport]
[Guid("6D4B9C3E-CC05-493F-85E2-43D1006DF96A")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IProbeWindows
{
    // slot 7: propget Item(Index) → Window*
    [return: MarshalAs(UnmanagedType.Interface)]
    IProbeWindow GetItem(uint index);

    // slot 8
    uint Count { get; }

    // slot 9: propget _NewEnum → IUnknown*. Not used: the probe walks Item(i), which also measures the index base.
    [Obsolete("Spike 4.12: placeholder for the _NewEnum slot; the probe enumerates with Item(i).", error: true)]
    void NewEnumPlaceholder();

    // slot 10: propget CurrentWindow → Window*
    IProbeWindow CurrentWindow { [return: MarshalAs(UnmanagedType.Interface)] get; }
}

/// <summary>
/// Spike 4.12: OneNote's <c>Window</c> (dual), slots 7–22 in type-library order. Only getters and
/// <see cref="NavigateTo"/> are callable. The setters (<c>FullPageView</c>, <c>Active</c>, <c>DockedLocation</c>),
/// <c>Application</c>, <c>NavigateToUrl</c> and <c>SetDockedLocation</c> are declared only as placeholders that keep the
/// vtable in order and can't be called: each is <see cref="ObsoleteAttribute"/> with <c>error: true</c>, so a call to
/// one doesn't compile (plan, Never).
/// </summary>
[ComImport]
[Guid("8E8304B8-CBD1-44F8-B0E8-89C625B2002E")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IProbeWindow
{
    // slot 7: HWND as an unsigned 64-bit value
    ulong WindowHandle { get; }

    // slot 8
    string CurrentPageId { [return: MarshalAs(UnmanagedType.BStr)] get; }

    // slot 9
    string CurrentSectionId { [return: MarshalAs(UnmanagedType.BStr)] get; }

    // slot 10
    string CurrentSectionGroupId { [return: MarshalAs(UnmanagedType.BStr)] get; }

    // slot 11
    string CurrentNotebookId { [return: MarshalAs(UnmanagedType.BStr)] get; }

    // slot 12 (user-visible). Called only through TestNotebook.WindowNavigateToCall, after the guard.
    void NavigateTo(
        [MarshalAs(UnmanagedType.BStr)] string bstrHierarchyObjectID,
        [MarshalAs(UnmanagedType.BStr)] string bstrObjectID);

    // slot 13
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool GetFullPageView();

    // slot 14: propput FullPageView
    [Obsolete("Spike 4.12: window setters are never called (plan, Never).", error: true)]
    void SetFullPageViewPlaceholder();

    // slot 15
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool GetActive();

    // slot 16: propput Active
    [Obsolete("Spike 4.12: window setters are never called (plan, Never).", error: true)]
    void SetActivePlaceholder();

    // slot 17: DockLocation (dlDefault -1, dlNone 0, dlLeft 1, dlRight 2, dlTop 3, dlBottom 4)
    int GetDockedLocation();

    // slot 18: propput DockedLocation
    [Obsolete("Spike 4.12: window setters are never called (plan, Never).", error: true)]
    void SetDockedLocationPlaceholder();

    // slot 19: propget Application → IApplication*. Not read (it would be one more wrapper to release, for nothing).
    [Obsolete("Spike 4.12: Window.Application is not read.", error: true)]
    void ApplicationPlaceholder();

    // slot 20
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool GetSideNote();

    // slot 21: NavigateToUrl(bstr)
    [Obsolete("Spike 4.12: Window.NavigateToUrl is never called (plan, Never).", error: true)]
    void NavigateToUrlPlaceholder();

    // slot 22: SetDockedLocation(DockLocation, POINT)
    [Obsolete("Spike 4.12: SetDockedLocation is never called (plan, Never).", error: true)]
    void SetDockedLocationMethodPlaceholder();
}
