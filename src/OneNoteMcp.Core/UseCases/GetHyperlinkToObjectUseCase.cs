using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>GetHyperlinkToObject</c>: the <c>onenote:</c> link to a hierarchy object, or to an object on a page when
/// <c>pageContentObjectId</c> is given, unchanged in <c>meta.link</c>. It never opens the link (AD-11).
/// AD-21: on a page in a password-protected section (or an object on such a page) it needs a read grant, checked
/// before the call; on the section itself, a section group or a notebook it needs none.
/// </summary>
public sealed class GetHyperlinkToObjectUseCase(IOneNoteGateway gateway, ReadAccess access) : IUseCase
{
    public GetHyperlinkToObjectUseCase(IOneNoteGateway gateway)
        : this(gateway, ReadAccess.WithoutStoredGrants(gateway))
    {
    }

    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string hierarchyId, string pageContentObjectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var granted = await access.CheckObjectAsync(context, hierarchyId ?? string.Empty, ct).ConfigureAwait(false);

        var link = await gateway.GetHyperlinkToObjectAsync(hierarchyId ?? string.Empty, pageContentObjectId ?? string.Empty, ct).ConfigureAwait(false);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, Link: link ?? string.Empty), granted.Warnings);
    }
}
