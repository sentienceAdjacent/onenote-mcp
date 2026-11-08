using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>list_grants</c>: the server-only tool that shows stored grants (AD-6, AD-14 <c>verb_noun</c>). Thin (AD-5).</summary>
[McpServerToolType]
public sealed class ListGrantsTool(ToolRunner runner, ListGrantsUseCase useCase)
{
    public const string Name = "list_grants";

    [McpServerTool(
        Name = Name,
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [McpMeta(ToolMetaKeys.MaxResultSizeChars, ToolMetaKeys.MaxResultSizeCharsValue)]
    [Description(
        "Read-only. Lists the stored grants plus this server process's live once grants, in meta.grants. No tool can create, change or remove a grant: only the user can, through a dialog or the OneNoteMcp grant command. "
        + "Today these are read grants for password-protected sections (kind readSection): {kind, key (the section's path), name, notebookKey, idAtGrant, grantedAt, grantedVia (elicitation or cli), lifetime (once, hours or untilRevoked), status (active, sectionNotOpen or expired), expiresAt?}. "
        + "A grant the user gave in the dialog with \"This section, for N minutes\" (lifetime once) is listed while its window lasts, with its expiresAt; it lives only in this server's memory and can't be revoked. "
        + "The user revokes a stored grant with `OneNoteMcp grant --revoke --read-section \"<section name>\"`. "
        + "Success: {ok:true, meta:{correlationId, grants}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> ListGrants(CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, ct),
            cancellationToken);
}
