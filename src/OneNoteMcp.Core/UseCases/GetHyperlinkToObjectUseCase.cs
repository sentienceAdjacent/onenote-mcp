using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>GetHyperlinkToObject</c>: the <c>onenote:</c> link to a hierarchy object, or to an object on a page when
/// <c>pageContentObjectId</c> is given, unchanged in <c>meta.link</c>. It never opens the link (AD-11).
/// </summary>
public sealed class GetHyperlinkToObjectUseCase(IOneNoteGateway gateway) : IUseCase
{
    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string hierarchyId, string pageContentObjectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var link = await gateway.GetHyperlinkToObjectAsync(hierarchyId ?? string.Empty, pageContentObjectId ?? string.Empty, ct).ConfigureAwait(false);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, Link: link ?? string.Empty), []);
    }
}
