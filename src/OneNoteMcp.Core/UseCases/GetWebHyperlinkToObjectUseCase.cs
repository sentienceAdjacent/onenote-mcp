using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>GetWebHyperlinkToObject</c>: the web link to an object, unchanged in <c>meta.link</c>. For a notebook that isn't
/// stored online OneNote returns an empty string rather than an error (observed 2026-11-04), so an empty link is a
/// degraded read (AD-4): <c>ok: true</c>, <c>meta.link: ""</c> and the <c>webLinkUnavailable</c> warning.
/// </summary>
public sealed class GetWebHyperlinkToObjectUseCase(IOneNoteGateway gateway) : IUseCase
{
    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string hierarchyId, string pageContentObjectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var id = hierarchyId ?? string.Empty;
        var link = await gateway.GetWebHyperlinkToObjectAsync(id, pageContentObjectId ?? string.Empty, ct).ConfigureAwait(false) ?? string.Empty;
        IReadOnlyList<ToolWarning> warnings = link.Length == 0
            ?
            [
                new ToolWarning(
                    ToolWarnings.WebLinkUnavailable,
                    $"OneNote returned no web link for {id}. It gives one only for objects in notebooks stored on OneDrive or SharePoint.",
                    "Use get_hyperlink_to_object for a onenote: link instead."),
            ]
            : [];
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, Link: link), warnings);
    }
}
