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

    [Fact]
    public void ClsidIsTheTypeLibraryApplicationCoclass() =>
        Assert.Equal(new Guid("D7FAC39E-7FF1-49AA-98CF-A1DDD316337E"), OneNoteClsid.Application);
}
