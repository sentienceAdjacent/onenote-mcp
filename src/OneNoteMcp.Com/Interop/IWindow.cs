using System.Runtime.InteropServices;

namespace OneNoteMcp.Com.Interop;

/// <summary>
/// OneNote's <c>Window</c> (dual), slots 7-22 in type-library order, as spike 4.12 measured them. Only the getters and
/// <see cref="NavigateTo"/> are callable. The setters (<c>FullPageView</c>, <c>Active</c>, <c>DockedLocation</c>),
/// <c>Application</c>, <c>NavigateToUrl</c> (story 4.7) and <c>SetDockedLocation</c> are declared only as placeholders that
/// keep the vtable in order and can't be called: each is <see cref="ObsoleteAttribute"/> with <c>error: true</c>, so a call
/// to one doesn't compile (spine AD-11: the window setters aren't exposed).
/// </summary>
[ComImport]
[Guid("8E8304B8-CBD1-44F8-B0E8-89C625B2002E")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IWindow
{
    // slot 7: the inner pane's HWND as an unsigned 64-bit value, not the top-level frame (spike 4.12 W1)
    ulong WindowHandle { get; }

    // slot 8: "" in a locked section (W5)
    string CurrentPageId { [return: MarshalAs(UnmanagedType.BStr)] get; }

    // slot 9
    string CurrentSectionId { [return: MarshalAs(UnmanagedType.BStr)] get; }

    // slot 10
    string CurrentSectionGroupId { [return: MarshalAs(UnmanagedType.BStr)] get; }

    // slot 11
    string CurrentNotebookId { [return: MarshalAs(UnmanagedType.BStr)] get; }

    // slot 12 (user-visible): only navigate_to with its window argument calls it, on the user's request (AD-11)
    void NavigateTo(
        [MarshalAs(UnmanagedType.BStr)] string bstrHierarchyObjectID,
        [MarshalAs(UnmanagedType.BStr)] string bstrObjectID);

    // slot 13: propget FullPageView
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool GetFullPageView();

    // slot 14: propput FullPageView
    [Obsolete("The window setters are never called (spine AD-11).", error: true)]
    void SetFullPageViewPlaceholder();

    // slot 15: propget Active
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool GetActive();

    // slot 16: propput Active
    [Obsolete("The window setters are never called (spine AD-11).", error: true)]
    void SetActivePlaceholder();

    // slot 17: propget DockedLocation (DockLocation: dlDefault -1, dlNone 0, dlLeft 1, dlRight 2, dlTop 3, dlBottom 4)
    int GetDockedLocation();

    // slot 18: propput DockedLocation
    [Obsolete("The window setters are never called (spine AD-11).", error: true)]
    void SetDockedLocationPlaceholder();

    // slot 19: propget Application returns IApplication*. Not read: one more wrapper to release, for nothing.
    [Obsolete("Window.Application is not read.", error: true)]
    void ApplicationPlaceholder();

    // slot 20: propget SideNote (a quick note, spike 4.12 W3)
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool GetSideNote();

    // slot 21: NavigateToUrl(bstr). Story 4.7's.
    [Obsolete("Window.NavigateToUrl is not used before story 4.7.", error: true)]
    void NavigateToUrlPlaceholder();

    // slot 22: SetDockedLocation(DockLocation, POINT)
    [Obsolete("SetDockedLocation is never called (spine AD-11).", error: true)]
    void SetDockedLocationMethodPlaceholder();
}
