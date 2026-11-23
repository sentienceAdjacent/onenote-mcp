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
        "SetDockedLocationPlaceholder", "ApplicationPlaceholder", "GetSideNote", "NavigateToUrl",
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
    public void WindowInterfaces_TypeTheirMarshalling_AndOnlyTheGettersNavigateToAndNavigateToUrlAreCallable()
    {
        foreach (var method in DeclaredMethods(typeof(IWindows)).Concat(DeclaredMethods(typeof(IWindow))))
        {
            if (method.Name.EndsWith("Placeholder", StringComparison.Ordinal))
            {
                // The setters, _NewEnum, Application and SetDockedLocation can't be called: a call doesn't compile (story 4.7 made NavigateToUrl callable).
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

    [Fact]
    public void ClsidIsTheTypeLibraryApplicationCoclass() =>
        Assert.Equal(new Guid("D7FAC39E-7FF1-49AA-98CF-A1DDD316337E"), OneNoteClsid.Application);
}
