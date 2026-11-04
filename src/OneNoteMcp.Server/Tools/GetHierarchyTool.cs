using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// Tracer tool: <c>GetHierarchy</c> at notebooks scope. Never throws. Errors come back as an
/// <c>isError</c> result whose text is <c>code: message</c>; the AD-9 envelope replaces this in story 3.
/// </summary>
[McpServerToolType]
public sealed class GetHierarchyTool(IOneNoteGateway gateway)
{
    [McpServerTool(Name = "get_hierarchy", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read-only. Returns the OneNote notebooks list as OneNote 2013-schema hierarchy XML, unchanged.")]
    public async Task<CallToolResult> GetHierarchy(
        [Description("OneNote object ID to start from. Omit or leave empty for all notebooks.")] string startNodeId = "",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var xml = await gateway.GetHierarchyAsync(startNodeId ?? string.Empty, HierarchyScope.Notebooks, cancellationToken);
            return Text(xml, isError: false);
        }
        catch (OneNoteException ex)
        {
            return Text($"{ex.Code}: {ex.Message}", isError: true);
        }
        catch (OperationCanceledException)
        {
            return Text($"{ErrorCode.Internal}: The call was cancelled.", isError: true);
        }
#pragma warning disable CA1031 // Tools never throw (AD-9): anything unexpected becomes Internal.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Text($"{ErrorCode.Internal}: Unexpected {ex.GetType().Name}.", isError: true);
        }
    }

    private static CallToolResult Text(string text, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };
}
