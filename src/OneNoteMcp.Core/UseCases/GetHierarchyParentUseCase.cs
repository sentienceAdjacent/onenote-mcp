using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>GetHierarchyParent</c>: one call, returning the parent's ID unchanged in <c>meta.parentId</c>. At a notebook OneNote
/// returns an empty string, the end of the chain (AD-6), which is returned as is. Walking the chain is the caller's.
/// </summary>
public sealed class GetHierarchyParentUseCase(IOneNoteGateway gateway) : IUseCase
{
    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string objectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var parentId = await gateway.GetHierarchyParentAsync(objectId ?? string.Empty, ct).ConfigureAwait(false);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, ParentId: parentId ?? string.Empty), []);
    }
}
