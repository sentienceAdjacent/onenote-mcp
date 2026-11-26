using System.Runtime.InteropServices;

namespace OneNoteMcp.Com.Interop;

/// <summary>
/// Hand-declared OneNote 15 <c>IQuickFilingDialog</c> (dual), the picker <see cref="IApplication.QuickFiling"/> returns as
/// IDispatch (story 4.9; spike 4.13, type library read 2026-11-17). Slots 7-29, in the type library's order: never reorder,
/// insert or remove members (<c>InteropLayoutTests</c> guards it).
/// <para>
/// Internal, as every interop type is (AD-2): only <see cref="ComGateway"/> casts to it, on its STA thread, and the
/// <see cref="QuickFilingCallback"/> casts the dialog OneNote passes back. The picker is configured only through
/// <see cref="Title"/>, <see cref="Description"/>, <see cref="TreeDepth"/> (always <see cref="HierarchyElement.heSections"/>)
/// and one <see cref="AddButton"/>, shown with <see cref="Run"/>, and answered through <see cref="SelectedItem"/> and
/// <see cref="PressedButton"/> (spine Coverage, QuickFiling dialog's members). Every other member, <c>ParentWindowHandle</c> and
/// <c>WindowHandle</c> included, is declared only as a placeholder that keeps the vtable in order and can't be called: each is
/// <see cref="ObsoleteAttribute"/> with <c>error: true</c>, so a call doesn't compile.
/// </para>
/// </summary>
[ComImport]
[Guid("1D12BD3F-89B6-4077-AA2C-C9DC2BCA42F9")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IQuickFilingDialog
{
    // slots 7/8: get/put Title
    string Title { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }

    // slots 9/10: get/put Description
    string Description { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }

    // slot 11: get CheckboxText
    [Obsolete("Story 4.9: the picker's checkbox is not used.", error: true)]
    void GetCheckboxTextPlaceholder();

    // slot 12: put CheckboxText
    [Obsolete("Story 4.9: the picker's checkbox is not used.", error: true)]
    void SetCheckboxTextPlaceholder();

    // slot 13: get CheckboxState
    [Obsolete("Story 4.9: the picker's checkbox is not used.", error: true)]
    void GetCheckboxStatePlaceholder();

    // slot 14: put CheckboxState
    [Obsolete("Story 4.9: the picker's checkbox is not used.", error: true)]
    void SetCheckboxStatePlaceholder();

    // slot 15: get WindowHandle
    [Obsolete("Story 4.9: the picker's own window handle is not read.", error: true)]
    void GetWindowHandlePlaceholder();

    // slots 16/17: get/put TreeDepth
    HierarchyElement TreeDepth { get; set; }

    // slot 18: get ParentWindowHandle
    [Obsolete("Story 4.9: the picker is unparented (spine AD-7 AppState quick_filing).", error: true)]
    void GetParentWindowHandlePlaceholder();

    // slot 19: put ParentWindowHandle
    [Obsolete("Story 4.9: the picker is unparented (spine AD-7 AppState quick_filing).", error: true)]
    void SetParentWindowHandlePlaceholder();

    // slot 20: get Position (POINT)
    [Obsolete("Story 4.9: the picker's position is not used.", error: true)]
    void GetPositionPlaceholder();

    // slot 21: put Position (POINT)
    [Obsolete("Story 4.9: the picker's position is not used.", error: true)]
    void SetPositionPlaceholder();

    // slot 22: SetRecentResults(RecentResultType, bool, bool, bool)
    [Obsolete("Story 4.9: SetRecentResults is not used.", error: true)]
    void SetRecentResultsPlaceholder();

    // slot 23: AddButton(bstr, HierarchyElement allowed, HierarchyElement allowedReadOnly, bool default)
    void AddButton(
        [MarshalAs(UnmanagedType.BStr)] string bstrText,
        HierarchyElement allowedElements,
        HierarchyElement allowedReadOnlyElements,
        [MarshalAs(UnmanagedType.VariantBool)] bool fDefault);

    // slot 24: Run(IQuickFilingDialogCallback). Returns at once; the answer arrives on the callback (spike 4.13 F2).
    void Run([MarshalAs(UnmanagedType.Interface)] IQuickFilingDialogCallback piCallback);

    // slot 25: get SelectedItem
    string SelectedItem { [return: MarshalAs(UnmanagedType.BStr)] get; }

    // slot 26: get PressedButton (0xFFFFFFFF for Cancel or Esc, spike 4.13 F5)
    uint PressedButton { get; }

    // slot 27: put TreeCollapsedState
    [Obsolete("Story 4.9: TreeCollapsedState is not used.", error: true)]
    void SetTreeCollapsedStatePlaceholder();

    // slot 28: put NotebookFilterOut
    [Obsolete("Story 4.9: NotebookFilterOut is not used.", error: true)]
    void SetNotebookFilterOutPlaceholder();

    // slot 29: AddInitialEditor(bstr)
    [Obsolete("Story 4.9: AddInitialEditor is not used.", error: true)]
    void AddInitialEditorPlaceholder();
}
