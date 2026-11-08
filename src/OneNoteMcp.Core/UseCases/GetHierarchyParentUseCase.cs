using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>GetHierarchyParent</c>: one call, returning the parent's ID unchanged in <c>meta.parentId</c>. At a notebook OneNote
/// returns an empty string, the end of the chain (AD-6), which is returned as is. Walking the chain is the caller's.
/// AD-21: on a page in a password-protected section it needs a read grant, checked
/// before the call; on the section itself, a section group or a notebook it needs none.
/// </summary>
public sealed class GetHierarchyParentUseCase(IOneNoteGateway gateway, ReadAccess access) : IUseCase
{
    public GetHierarchyParentUseCase(IOneNoteGateway gateway)
        : this(gateway, ReadAccess.WithoutStoredGrants(gateway))
    {
    }

    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string objectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var granted = await access.CheckObjectAsync(context, objectId ?? string.Empty, ct).ConfigureAwait(false);

        var parentId = await gateway.GetHierarchyParentAsync(objectId ?? string.Empty, ct).ConfigureAwait(false);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, ParentId: parentId ?? string.Empty), granted.Warnings);
    }
}
