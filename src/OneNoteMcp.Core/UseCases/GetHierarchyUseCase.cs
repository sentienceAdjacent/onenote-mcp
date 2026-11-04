using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.UseCases;

/// <summary><c>GetHierarchy</c> at notebooks scope. Returns the XML unchanged (AD-4) and throws only <see cref="OneNoteException"/>.</summary>
public sealed class GetHierarchyUseCase(IOneNoteGateway gateway) : IUseCase
{
    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string startNodeId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var xml = await gateway.GetHierarchyAsync(startNodeId ?? string.Empty, HierarchyScope.Notebooks, ct).ConfigureAwait(false);
        return ToolSuccess.WithXml(xml, new ToolMeta(context.CorrelationId));
    }
}
