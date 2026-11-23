using System.Reflection;
using System.Runtime.InteropServices;
using OneNoteMcp.Com.Interop;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>CI guard for AD-1: the hand-declared vtable must match the OneNote 15 type library slot order.</summary>
public sealed class InteropLayoutTests
{
    // Slots 7-35 of IApplication, in the order of the registered type library.
    private static readonly string[] ExpectedSlots =
    [
        "GetHierarchy", "UpdateHierarchy", "OpenHierarchy", "DeleteHierarchy", "CreateNewPage", "CloseNotebook",
        "GetHierarchyParent", "GetPageContent", "UpdatePageContent", "GetBinaryPageContent", "DeletePageContent",
        "NavigateTo", "NavigateToUrl", "Publish", "OpenPackage", "GetHyperlinkToObject", "FindPages", "FindMeta",
        "GetSpecialLocation", "MergeFiles", "QuickFiling", "SyncHierarchy", "SetFilingLocation", "get_Windows",
        "get_Dummy1", "MergeSections", "get_COMAddIns", "get_LanguageSettings", "GetWebHyperlinkToObject",
    ];

    private static MethodInfo[] DeclaredMethods() =>
        [.. typeof(IApplication).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(m => m.MetadataToken)];

    [Fact]
    public void IApplication_DeclaredMethodOrder_EqualsTypeLibrarySlots() =>
        Assert.Equal(ExpectedSlots, DeclaredMethods().Select(m => m.Name));

    [Fact]
    public void IApplication_IsDualComImportWithTypeLibraryIid()
    {
        var type = typeof(IApplication);
        Assert.True(type.IsImport);
        Assert.Equal(new Guid("452AC71A-B655-4967-A208-A4CC39DD7949"), type.GUID);
        Assert.Equal(ComInterfaceType.InterfaceIsDual, type.GetCustomAttribute<InterfaceTypeAttribute>()!.Value);
    }

    [Fact]
    public void IApplication_NoMemberPreservesSig() =>
        Assert.All(DeclaredMethods(), m => Assert.False(
            m.MethodImplementationFlags.HasFlag(MethodImplAttributes.PreserveSig), m.Name));

    [Fact]
    public void IApplication_StringsAreBstr_AndBoolsAreVariantBool()
    {
        foreach (var method in DeclaredMethods())
        {
            foreach (var p in method.GetParameters().Append(method.ReturnParameter))
            {
                var type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
                var marshalAs = p.GetCustomAttribute<MarshalAsAttribute>();
                if (type == typeof(string))
                {
                    Assert.Equal(UnmanagedType.BStr, marshalAs?.Value);
                }
                else if (type == typeof(bool))
                {
                    Assert.Equal(UnmanagedType.VariantBool, marshalAs?.Value);
                }
                else if (type == typeof(object))
                {
                    Assert.Equal(UnmanagedType.IDispatch, marshalAs?.Value);
                }
            }
        }
    }

    // Story 4.6 (spike 4.12, type library read 2026-11-16): slots 7-10 of Windows and 7-22 of Window.
    private static readonly string[] ExpectedWindowsSlots = ["GetItem", "get_Count", "NewEnumPlaceholder", "get_CurrentWindow"];

    private static readonly string[] ExpectedWindowSlots =
    [
        "get_WindowHandle", "get_CurrentPageId", "get_CurrentSectionId", "get_CurrentSectionGroupId", "get_CurrentNotebookId",
        "NavigateTo", "GetFullPageView", "SetFullPageViewPlaceholder", "GetActive", "SetActivePlaceholder", "GetDockedLocation",
        "SetDockedLocationPlaceholder", "ApplicationPlaceholder", "GetSideNote", "NavigateToUrlPlaceholder",
        "SetDockedLocationMethodPlaceholder",
    ];

    private static MethodInfo[] DeclaredMethods(Type type) =>
        [.. type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(m => m.MetadataToken)];

    [Fact]
    public void IWindows_DeclaredMethodOrder_EqualsTypeLibrarySlots() =>
        Assert.Equal(ExpectedWindowsSlots, DeclaredMethods(typeof(IWindows)).Select(m => m.Name));

    [Fact]
    public void IWindow_DeclaredMethodOrder_EqualsTypeLibrarySlots() =>
        Assert.Equal(ExpectedWindowSlots, DeclaredMethods(typeof(IWindow)).Select(m => m.Name));

    [Theory]
    [InlineData(typeof(IWindows), "6D4B9C3E-CC05-493F-85E2-43D1006DF96A")]
    [InlineData(typeof(IWindow), "8E8304B8-CBD1-44F8-B0E8-89C625B2002E")]
    public void WindowInterfaces_AreDualComImportsWithTypeLibraryIids(Type type, string iid)
    {
        Assert.True(type.IsImport);
        Assert.Equal(new Guid(iid), type.GUID);
        Assert.Equal(ComInterfaceType.InterfaceIsDual, type.GetCustomAttribute<InterfaceTypeAttribute>()!.Value);
        Assert.All(DeclaredMethods(type), m => Assert.False(m.MethodImplementationFlags.HasFlag(MethodImplAttributes.PreserveSig), m.Name));
    }

    [Fact]
    public void WindowInterfaces_TypeTheirMarshalling_AndOnlyTheGettersAndNavigateToAreCallable()
    {
        foreach (var method in DeclaredMethods(typeof(IWindows)).Concat(DeclaredMethods(typeof(IWindow))))
        {
            if (method.Name.EndsWith("Placeholder", StringComparison.Ordinal))
            {
                // The setters, _NewEnum, Application, NavigateToUrl and SetDockedLocation can't be called: a call doesn't compile.
                Assert.True(method.GetCustomAttribute<ObsoleteAttribute>()?.IsError, method.Name);
                continue;
            }

            Assert.Null(method.GetCustomAttribute<ObsoleteAttribute>());
            foreach (var p in method.GetParameters().Append(method.ReturnParameter))
            {
                var marshalAs = p.GetCustomAttribute<MarshalAsAttribute>();
                if (p.ParameterType == typeof(string))
                {
                    Assert.Equal(UnmanagedType.BStr, marshalAs?.Value);
                }
                else if (p.ParameterType == typeof(bool))
                {
                    Assert.Equal(UnmanagedType.VariantBool, marshalAs?.Value);
                }
                else if (p.ParameterType == typeof(IWindow))
                {
                    Assert.Equal(UnmanagedType.Interface, marshalAs?.Value);
                }
            }
        }
    }

    // Story 4.9 (spike 4.13, type library read 2026-11-17): slots 7-29 of IQuickFilingDialog and 7 of its callback.
    private static readonly string[] ExpectedQuickFilingSlots =
    [
        "get_Title", "set_Title", "get_Description", "set_Description",
        "GetCheckboxTextPlaceholder", "SetCheckboxTextPlaceholder", "GetCheckboxStatePlaceholder", "SetCheckboxStatePlaceholder",
        "GetWindowHandlePlaceholder", "get_TreeDepth", "set_TreeDepth", "GetParentWindowHandlePlaceholder", "SetParentWindowHandlePlaceholder",
        "GetPositionPlaceholder", "SetPositionPlaceholder", "SetRecentResultsPlaceholder", "AddButton", "Run",
        "get_SelectedItem", "get_PressedButton",
        "SetTreeCollapsedStatePlaceholder", "SetNotebookFilterOutPlaceholder", "AddInitialEditorPlaceholder",
    ];

    [Fact]
    public void IQuickFilingDialog_DeclaredMethodOrder_EqualsTypeLibrarySlots() =>
        Assert.Equal(ExpectedQuickFilingSlots, DeclaredMethods(typeof(IQuickFilingDialog)).Select(m => m.Name));

    [Fact]
    public void IQuickFilingDialogCallback_DeclaresOnDialogClosedOnly() =>
        Assert.Equal(["OnDialogClosed"], DeclaredMethods(typeof(IQuickFilingDialogCallback)).Select(m => m.Name));

    [Theory]
    [InlineData(typeof(IQuickFilingDialog), "1D12BD3F-89B6-4077-AA2C-C9DC2BCA42F9", false)]
    [InlineData(typeof(IQuickFilingDialogCallback), "627EA7B4-95B5-4980-84C1-9D20DA4460B1", true)]
    public void QuickFilingInterfaces_AreDualComImports_WithTypeLibraryIids_AndOnlyTheCallbackIsPublic(Type type, string iid, bool isPublic)
    {
        // Decision OQ2: COM sees a managed object only through public interfaces, so the callback's is public; the dialog's stays internal (AD-2).
        Assert.True(type.IsImport);
        Assert.Equal(isPublic, type.IsPublic);
        Assert.Equal(new Guid(iid), type.GUID);
        Assert.Equal(ComInterfaceType.InterfaceIsDual, type.GetCustomAttribute<InterfaceTypeAttribute>()!.Value);
        Assert.All(DeclaredMethods(type), m => Assert.False(m.MethodImplementationFlags.HasFlag(MethodImplAttributes.PreserveSig), m.Name));
    }

    [Fact]
    public void QuickFilingCallback_IsPublicSealedComVisible_WithAnInternalConstructorOnly()
    {
        var type = typeof(OneNoteMcp.Com.QuickFilingCallback);
        Assert.True(type.IsPublic);
        Assert.True(type.IsSealed);
        Assert.True(type.GetCustomAttribute<ComVisibleAttribute>()!.Value);
        Assert.Equal(ClassInterfaceType.None, type.GetCustomAttribute<ClassInterfaceAttribute>()!.Value);
        Assert.Empty(type.GetConstructors()); // no public constructor
        Assert.True(typeof(IQuickFilingDialogCallback).IsAssignableFrom(type));
    }

    [Fact]
    public void QuickFilingDialog_OnlyTheAllowedMembersAreCallable_AndTheirMarshallingIsTyped()
    {
        // Spine Coverage: Title, Description, TreeDepth, AddButton, Run, SelectedItem, PressedButton; never ParentWindowHandle.
        var callable = new List<string>();
        foreach (var method in DeclaredMethods(typeof(IQuickFilingDialog)))
        {
            if (method.Name.EndsWith("Placeholder", StringComparison.Ordinal))
            {
                Assert.True(method.GetCustomAttribute<ObsoleteAttribute>()?.IsError, method.Name);
                continue;
            }

            Assert.Null(method.GetCustomAttribute<ObsoleteAttribute>());
            callable.Add(method.Name);
            foreach (var p in method.GetParameters().Append(method.ReturnParameter))
            {
                var marshalAs = p.GetCustomAttribute<MarshalAsAttribute>();
                if (p.ParameterType == typeof(string))
                {
                    Assert.Equal(UnmanagedType.BStr, marshalAs?.Value);
                }
                else if (p.ParameterType == typeof(bool))
                {
                    Assert.Equal(UnmanagedType.VariantBool, marshalAs?.Value);
                }
                else if (p.ParameterType == typeof(IQuickFilingDialogCallback))
                {
                    Assert.Equal(UnmanagedType.Interface, marshalAs?.Value);
                }
            }
        }

        Assert.Equal(
            ["AddButton", "Run", "get_Description", "get_PressedButton", "get_SelectedItem", "get_Title", "get_TreeDepth", "set_Description", "set_Title", "set_TreeDepth"],
            callable.Order(StringComparer.Ordinal));
        var dialog = typeof(IQuickFilingDialogCallback).GetMethod("OnDialogClosed")!.GetParameters().Single();
        Assert.Equal(typeof(object), dialog.ParameterType);
        Assert.Equal(UnmanagedType.IDispatch, dialog.GetCustomAttribute<MarshalAsAttribute>()?.Value);
        Assert.Equal([0, 1, 2, 4, 8], Enum.GetValues<HierarchyElement>().Select(e => (int)e));
    }

    [Fact]
    public void ClsidIsTheTypeLibraryApplicationCoclass() =>
        Assert.Equal(new Guid("D7FAC39E-7FF1-49AA-98CF-A1DDD316337E"), OneNoteClsid.Application);
}
