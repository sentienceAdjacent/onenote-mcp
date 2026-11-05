using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.UseCases;

/// <summary><c>GetSpecialLocation</c>: the path OneNote keeps for a location, unchanged in <c>meta.path</c>.</summary>
public sealed class GetSpecialLocationUseCase(IOneNoteGateway gateway) : IUseCase
{
    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, SpecialLocation location, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = await gateway.GetSpecialLocationAsync(location, ct).ConfigureAwait(false);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, Path: path ?? string.Empty), []);
    }
}
