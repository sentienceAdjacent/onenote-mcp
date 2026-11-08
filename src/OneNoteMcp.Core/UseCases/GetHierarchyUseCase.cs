using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>GetHierarchy</c> at any scope. Returns the XML unchanged (AD-4), with one <c>sectionLocked</c> warning per locked
/// section in it, except for AD-21's withholding. Throws only <see cref="OneNoteException"/>.
/// <list type="bullet">
/// <item>A broad read, from above a password-protected section, keeps the section's <c>one:Section</c> and removes its
/// <c>one:Page</c> elements unless there is a read grant for it, listing it in <c>meta.withheld</c> with a
/// <c>readNotGranted</c> warning. It never asks, and finds the sections in its own XML with no extra COM call.</item>
/// <item>A start node that is a protected section, at scope <c>children</c> or <c>pages</c>, or a page in one, is
/// target-scoped: it needs a read grant, asked for through the dialog when the client supports it, and is
/// <c>ReadNotGranted</c> without one. The section itself at scope <c>self</c>, <c>notebooks</c> or <c>sections</c>
/// returns no pages and needs none.</item>
/// </list>
/// </summary>
public sealed class GetHierarchyUseCase(IOneNoteGateway gateway, ReadAccess access) : IUseCase
{
    public GetHierarchyUseCase(IOneNoteGateway gateway)
        : this(gateway, ReadAccess.WithoutStoredGrants(gateway))
    {
    }

    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string startNodeId, HierarchyScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var start = startNodeId ?? string.Empty;
        var xml = await gateway.GetHierarchyAsync(start, scope, ct).ConfigureAwait(false);
        var root = Readable(() => HierarchyNode.Root(xml));

        // AD-21 runs before the locked check and before anything is returned.
        IReadOnlyList<ToolWarning> accessWarnings = [];
        IReadOnlyList<string>? withheld = null;
        LargeResults.ResultGrants grants = LargeResults.ResultGrants.None;
        if (root.Kind == HierarchyKind.Page)
        {
            var target = await access.CheckObjectAsync(context, start, ct).ConfigureAwait(false);
            (accessWarnings, grants) = (target.Warnings, target.ResultGrants);
        }
        else if (root.Kind == HierarchyKind.Section && root.Encrypted)
        {
            if (scope is HierarchyScope.Children or HierarchyScope.Pages)
            {
                var target = await access.CheckSectionAsync(context, root, ct).ConfigureAwait(false);
                (accessWarnings, grants) = (target.Warnings, target.ResultGrants);
            }
        }
        else
        {
            // Pages appear only at scope pages here (children of a notebook or group are sections and groups).
            var broad = access.CheckBroad(Readable(() => EncryptedSections.Find(xml)), includesPages: scope == HierarchyScope.Pages);
            xml = Readable(() => SearchResultFilter.RemovePagesInSections(xml, broad.Withheld.ToHashSet(StringComparer.Ordinal))).Xml;
            (accessWarnings, withheld, grants) = (broad.Warnings, broad.Meta, broad.ResultGrants);
        }

        var locked = Readable(() => LockedSections.Find(xml));

        // The section's ID is the only detail in the message: never its name or anything else from the notebook (AD-9).
        var warnings = accessWarnings
            .Concat(locked.Select(id => new ToolWarning(
                ToolWarnings.SectionLocked,
                $"Section {id} is locked; its pages can't be read until it is unlocked in OneNote.",
                "Unlock the section in OneNote, then read it again. " + ToolErrors.AddInAccessOptionNote)))
            .ToList();
        return new ToolSuccess(xml, new ToolMeta(context.CorrelationId, Withheld: withheld), warnings)
        {
            Source = new ResultSource(ResultKind.HierarchyXml),
            Grants = grants,
        };
    }

    private static T Readable<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (OneNoteXmlException ex)
        {
            throw new OneNoteException(ErrorCode.InvalidXml, $"GetHierarchy returned XML the server can't read: {ex.Message}", innerException: ex);
        }
    }
}
