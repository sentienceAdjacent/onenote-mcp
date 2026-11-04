using System.Runtime.InteropServices;

namespace OneNoteMcp.Com.Interop;

/// <summary>
/// Hand-declared OneNote 15 <c>IApplication</c> (AD-1), matching the vtable of the registered type library
/// {0EA692EE-BB50-4E3C-AEF0-356D91732725} 1.1. <see cref="ComInterfaceType.InterfaceIsDual"/> keeps the
/// IUnknown/IDispatch slots 0-6 implicit, so the first member below is slot 7 and the last is slot 35.
/// Order is load-bearing: never reorder, insert or remove members (a CI test guards it).
/// Every parameter is declared, optional ones included, because they are still vtable arguments.
/// No member is [PreserveSig], so a failed HRESULT surfaces as COMException.
/// DateTime marshals as VT_DATE; callers pass UTC.
/// </summary>
[ComImport]
[Guid("452AC71A-B655-4967-A208-A4CC39DD7949")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IApplication
{
    // slot 7
    void GetHierarchy(
        [MarshalAs(UnmanagedType.BStr)] string bstrStartNodeID,
        HierarchyScope hsScope,
        [MarshalAs(UnmanagedType.BStr)] out string pbstrHierarchyXmlOut,
        XMLSchema xsSchema);

    // slot 8
    void UpdateHierarchy(
        [MarshalAs(UnmanagedType.BStr)] string bstrChangesXmlIn,
        XMLSchema xsSchema);

    // slot 9
    void OpenHierarchy(
        [MarshalAs(UnmanagedType.BStr)] string bstrPath,
        [MarshalAs(UnmanagedType.BStr)] string bstrRelativeToObjectID,
        [MarshalAs(UnmanagedType.BStr)] out string pbstrObjectID,
        CreateFileType cftIfNotExist);

    // slot 10
    void DeleteHierarchy(
        [MarshalAs(UnmanagedType.BStr)] string bstrObjectID,
        DateTime dateExpectedLastModified,
        [MarshalAs(UnmanagedType.VariantBool)] bool deletePermanently);

    // slot 11
    void CreateNewPage(
        [MarshalAs(UnmanagedType.BStr)] string bstrSectionID,
        [MarshalAs(UnmanagedType.BStr)] out string pbstrPageID,
        NewPageStyle npsNewPageStyle);

    // slot 12
    void CloseNotebook(
        [MarshalAs(UnmanagedType.BStr)] string bstrNotebookID,
        [MarshalAs(UnmanagedType.VariantBool)] bool force);

    // slot 13
    void GetHierarchyParent(
        [MarshalAs(UnmanagedType.BStr)] string bstrObjectID,
        [MarshalAs(UnmanagedType.BStr)] out string pbstrParentID);

    // slot 14
    void GetPageContent(
        [MarshalAs(UnmanagedType.BStr)] string bstrPageID,
        [MarshalAs(UnmanagedType.BStr)] out string pbstrPageXmlOut,
        PageInfo pageInfoToExport,
        XMLSchema xsSchema);

    // slot 15
    void UpdatePageContent(
        [MarshalAs(UnmanagedType.BStr)] string bstrPageChangesXmlIn,
        DateTime dateExpectedLastModified,
        XMLSchema xsSchema,
        [MarshalAs(UnmanagedType.VariantBool)] bool force);

    // slot 16
    void GetBinaryPageContent(
        [MarshalAs(UnmanagedType.BStr)] string bstrPageID,
        [MarshalAs(UnmanagedType.BStr)] string bstrCallbackID,
        [MarshalAs(UnmanagedType.BStr)] out string pbstrBinaryObjectB64Out);

    // slot 17
    void DeletePageContent(
        [MarshalAs(UnmanagedType.BStr)] string bstrPageID,
        [MarshalAs(UnmanagedType.BStr)] string bstrObjectID,
        DateTime dateExpectedLastModified,
        [MarshalAs(UnmanagedType.VariantBool)] bool force);

    // slot 18 (user-visible: takes focus, AD-11)
    void NavigateTo(
        [MarshalAs(UnmanagedType.BStr)] string bstrHierarchyObjectID,
        [MarshalAs(UnmanagedType.BStr)] string bstrObjectID,
        [MarshalAs(UnmanagedType.VariantBool)] bool fNewWindow);

    // slot 19 (user-visible, AD-11)
    void NavigateToUrl(
        [MarshalAs(UnmanagedType.BStr)] string bstrUrl,
        [MarshalAs(UnmanagedType.VariantBool)] bool fNewWindow);

    // slot 20
    void Publish(
        [MarshalAs(UnmanagedType.BStr)] string bstrHierarchyID,
        [MarshalAs(UnmanagedType.BStr)] string bstrTargetFilePath,
        PublishFormat pfPublishFormat,
        [MarshalAs(UnmanagedType.BStr)] string bstrCLSIDofExporter);

    // slot 21
    void OpenPackage(
        [MarshalAs(UnmanagedType.BStr)] string bstrPathPackage,
        [MarshalAs(UnmanagedType.BStr)] string bstrPathDest,
        [MarshalAs(UnmanagedType.BStr)] out string pbstrPathOut);

    // slot 22
    void GetHyperlinkToObject(
        [MarshalAs(UnmanagedType.BStr)] string bstrHierarchyID,
        [MarshalAs(UnmanagedType.BStr)] string bstrPageContentObjectID,
        [MarshalAs(UnmanagedType.BStr)] out string pbstrHyperlinkOut);

    // slot 23
    void FindPages(
        [MarshalAs(UnmanagedType.BStr)] string bstrStartNodeID,
        [MarshalAs(UnmanagedType.BStr)] string bstrSearchString,
        [MarshalAs(UnmanagedType.BStr)] out string pbstrHierarchyXmlOut,
        [MarshalAs(UnmanagedType.VariantBool)] bool fIncludeUnindexedPages,
        [MarshalAs(UnmanagedType.VariantBool)] bool fDisplay,
        XMLSchema xsSchema);

    // slot 24
    void FindMeta(
        [MarshalAs(UnmanagedType.BStr)] string bstrStartNodeID,
        [MarshalAs(UnmanagedType.BStr)] string bstrSearchStringName,
        [MarshalAs(UnmanagedType.BStr)] out string pbstrHierarchyXmlOut,
        [MarshalAs(UnmanagedType.VariantBool)] bool fIncludeUnindexedPages,
        XMLSchema xsSchema);

    // slot 25
    void GetSpecialLocation(
        SpecialLocation slToGet,
        [MarshalAs(UnmanagedType.BStr)] out string pbstrSpecialLocationPath);

    // slot 26
    void MergeFiles(
        [MarshalAs(UnmanagedType.BStr)] string bstrBaseFile,
        [MarshalAs(UnmanagedType.BStr)] string bstrClientFile,
        [MarshalAs(UnmanagedType.BStr)] string bstrServerFile,
        [MarshalAs(UnmanagedType.BStr)] string bstrTargetFile);

    // slot 27 (user-visible, AD-11). Returns IQuickFilingDialog, kept as IDispatch until a story needs it.
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object QuickFiling();

    // slot 28
    void SyncHierarchy([MarshalAs(UnmanagedType.BStr)] string bstrHierarchyID);

    // slot 29
    void SetFilingLocation(
        FilingLocation flToSet,
        FilingLocationType fltToSet,
        [MarshalAs(UnmanagedType.BStr)] string bstrFilingSectionID);

    // slot 30. Returns the Windows collection, kept as IDispatch; cast to IWindows for its members.
    object Windows { [return: MarshalAs(UnmanagedType.IDispatch)] get; }

    // slot 31
    bool Dummy1 { [return: MarshalAs(UnmanagedType.VariantBool)] get; }

    // slot 32
    void MergeSections(
        [MarshalAs(UnmanagedType.BStr)] string bstrSectionSourceId,
        [MarshalAs(UnmanagedType.BStr)] string bstrSectionDestinationId);

    // slot 33
    object COMAddIns { [return: MarshalAs(UnmanagedType.IDispatch)] get; }

    // slot 34
    object LanguageSettings { [return: MarshalAs(UnmanagedType.IDispatch)] get; }

    // slot 35
    void GetWebHyperlinkToObject(
        [MarshalAs(UnmanagedType.BStr)] string bstrHierarchyID,
        [MarshalAs(UnmanagedType.BStr)] string bstrPageContentObjectID,
        [MarshalAs(UnmanagedType.BStr)] out string pbstrHyperlinkOut);
}
