using System.Globalization;
using System.Runtime.InteropServices;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;
using CoreNewPageStyle = OneNoteMcp.Core.NewPageStyle;
using CorePageInfo = OneNoteMcp.Core.PageInfo;
using CoreScope = OneNoteMcp.Core.HierarchyScope;
using CoreSpecialLocation = OneNoteMcp.Core.SpecialLocation;
using InteropNewPageStyle = OneNoteMcp.Com.Interop.NewPageStyle;
using InteropPageInfo = OneNoteMcp.Com.Interop.PageInfo;
using InteropScope = OneNoteMcp.Com.Interop.HierarchyScope;
using InteropSpecialLocation = OneNoteMcp.Com.Interop.SpecialLocation;

namespace OneNoteMcp.Com;

public sealed partial class ComGateway
{
    public Task<string> GetHierarchyAsync(string startNodeId, CoreScope scope, CancellationToken ct)
    {
        var interopScope = MapScope(scope);
        return RunAsync(
            nameof(IApplication.GetHierarchy),
            app =>
            {
                app.GetHierarchy(startNodeId ?? string.Empty, interopScope, out var xml, XMLSchema.xs2013);
                return xml;
            },
            CallKind.Read,
            ct);
    }

    public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.GetHierarchyParent),
            app =>
            {
                app.GetHierarchyParent(objectId ?? string.Empty, out var parent);
                return parent;
            },
            CallKind.Read,
            ct);

    public Task<string> GetPageContentAsync(string pageId, CorePageInfo pageInfo, CancellationToken ct)
    {
        var interopInfo = MapPageInfo(pageInfo);
        return RunAsync(
            nameof(IApplication.GetPageContent),
            app =>
            {
                app.GetPageContent(pageId ?? string.Empty, out var xml, interopInfo, XMLSchema.xs2013);
                return xml;
            },
            CallKind.Read,
            ct);
    }

    public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.GetBinaryPageContent),
            app =>
            {
                app.GetBinaryPageContent(pageId ?? string.Empty, callbackId ?? string.Empty, out var base64);
                return base64;
            },
            CallKind.Read,
            ct);

    public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.FindPages),
            app =>
            {
                // fDisplay is always false: search never shows results in OneNote's UI (AD-11).
                app.FindPages(startNodeId ?? string.Empty, query ?? string.Empty, out var xml, includeUnindexed, fDisplay: false, XMLSchema.xs2013);
                return xml;
            },
            CallKind.Read,
            ct);

    public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.FindMeta),
            app =>
            {
                app.FindMeta(startNodeId ?? string.Empty, name ?? string.Empty, out var xml, includeUnindexed, XMLSchema.xs2013);
                return xml;
            },
            CallKind.Read,
            ct);

    public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.GetHyperlinkToObject),
            app =>
            {
                app.GetHyperlinkToObject(hierarchyId ?? string.Empty, pageContentObjectId ?? string.Empty, out var link);
                return link;
            },
            CallKind.Read,
            ct);

    public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.GetWebHyperlinkToObject),
            app =>
            {
                app.GetWebHyperlinkToObject(hierarchyId ?? string.Empty, pageContentObjectId ?? string.Empty, out var link);
                return link;
            },
            CallKind.Read,
            ct);

    public Task<string> GetSpecialLocationAsync(CoreSpecialLocation location, CancellationToken ct)
    {
        var interopLocation = MapSpecialLocation(location);
        return RunAsync(
            nameof(IApplication.GetSpecialLocation),
            app =>
            {
                app.GetSpecialLocation(interopLocation, out var path);
                return path;
            },
            CallKind.Read,
            ct);
    }
}
