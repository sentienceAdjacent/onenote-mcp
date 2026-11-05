using OneNoteMcp.Core.Results;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>GetHierarchy</c> at any scope. Returns the XML unchanged (AD-4), with one <c>sectionLocked</c> warning per locked
/// section in it. Throws only <see cref="OneNoteException"/>.
/// </summary>
public sealed class GetHierarchyUseCase(IOneNoteGateway gateway) : IUseCase
{
    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string startNodeId, HierarchyScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var xml = await gateway.GetHierarchyAsync(startNodeId ?? string.Empty, scope, ct).ConfigureAwait(false);

        IReadOnlyList<string> locked;
        try
        {
            locked = LockedSections.Find(xml);
        }
        catch (OneNoteXmlException ex)
        {
            throw new OneNoteException(ErrorCode.InvalidXml, $"GetHierarchy returned XML the server can't read: {ex.Message}", innerException: ex);
        }

        // The section's ID is the only detail in the message: never its name or anything else from the notebook (AD-9).
        var warnings = locked
            .Select(id => new ToolWarning(
                ToolWarnings.SectionLocked,
                $"Section {id} is locked; its pages can't be read until it is unlocked in OneNote.",
                "Unlock the section in OneNote, then read it again."))
            .ToList();
        return new ToolSuccess(xml, new ToolMeta(context.CorrelationId), warnings);
    }
}
