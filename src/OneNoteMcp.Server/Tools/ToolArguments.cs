using System.Text.Json.Serialization;
using OneNoteMcp.Core;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>get_hierarchy</c>'s <c>scope</c> on the wire. Its names are part of the tool contract (AD-14); Core's
/// <see cref="HierarchyScope"/> stays free of wire concerns.
/// </summary>
[JsonConverter(typeof(StrictStringEnumConverter<HierarchyScopeArgument>))]
public enum HierarchyScopeArgument
{
    [JsonStringEnumMemberName("self")]
    Self,

    [JsonStringEnumMemberName("children")]
    Children,

    [JsonStringEnumMemberName("notebooks")]
    Notebooks,

    [JsonStringEnumMemberName("sections")]
    Sections,

    [JsonStringEnumMemberName("pages")]
    Pages,
}

/// <summary><c>get_special_location</c>'s <c>location</c> on the wire (AD-14).</summary>
[JsonConverter(typeof(StrictStringEnumConverter<SpecialLocationArgument>))]
public enum SpecialLocationArgument
{
    [JsonStringEnumMemberName("backUpFolder")]
    BackUpFolder,

    [JsonStringEnumMemberName("unfiledNotesSection")]
    UnfiledNotesSection,

    [JsonStringEnumMemberName("defaultNotebookFolder")]
    DefaultNotebookFolder,
}

/// <summary><c>create_page</c>'s <c>style</c> on the wire (AD-14; Phill, 2026-11-10, 2.4 plan Q1).</summary>
[JsonConverter(typeof(StrictStringEnumConverter<PageStyleArgument>))]
public enum PageStyleArgument
{
    [JsonStringEnumMemberName("default")]
    Default,

    [JsonStringEnumMemberName("blankPageWithTitle")]
    BlankPageWithTitle,

    [JsonStringEnumMemberName("blankPageNoTitle")]
    BlankPageNoTitle,
}

/// <summary>Maps the wire enums to Core's. Total over each, so an unknown value is a bug, never a silent default.</summary>
public static class ToolArguments
{
    public static NewPageStyle ToCore(this PageStyleArgument style) => style switch
    {
        PageStyleArgument.Default => NewPageStyle.Default,
        PageStyleArgument.BlankPageWithTitle => NewPageStyle.BlankPageWithTitle,
        PageStyleArgument.BlankPageNoTitle => NewPageStyle.BlankPageNoTitle,
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown page style."),
    };

    public static HierarchyScope ToCore(this HierarchyScopeArgument scope) => scope switch
    {
        HierarchyScopeArgument.Self => HierarchyScope.Self,
        HierarchyScopeArgument.Children => HierarchyScope.Children,
        HierarchyScopeArgument.Notebooks => HierarchyScope.Notebooks,
        HierarchyScopeArgument.Sections => HierarchyScope.Sections,
        HierarchyScopeArgument.Pages => HierarchyScope.Pages,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown scope."),
    };

    public static SpecialLocation ToCore(this SpecialLocationArgument location) => location switch
    {
        SpecialLocationArgument.BackUpFolder => SpecialLocation.BackUpFolder,
        SpecialLocationArgument.UnfiledNotesSection => SpecialLocation.UnfiledNotesSection,
        SpecialLocationArgument.DefaultNotebookFolder => SpecialLocation.DefaultNotebookFolder,
        _ => throw new ArgumentOutOfRangeException(nameof(location), location, "Unknown location."),
    };
}

/// <summary>
/// A string-only enum converter: unlike <see cref="JsonStringEnumConverter{TEnum}"/>'s default, it rejects integers
/// (<c>1</c>, <c>"3"</c>, <c>99</c>), so a tool argument is always one of its declared names.
/// </summary>
public sealed class StrictStringEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(namingPolicy: null, allowIntegerValues: false)
    where TEnum : struct, Enum;
