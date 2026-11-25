using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary><c>QuickFiling</c> (story 4.9). Thin (AD-5): the policy is in <see cref="QuickFilingUseCase"/>. User-visible (AD-11).</summary>
[McpServerToolType]
public sealed class QuickFilingTool(ToolRunner runner, QuickFilingUseCase useCase)
{
    public const string Name = "quick_filing";

    [McpServerTool(
        Name = Name,
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(ToolEnvelope))]
    [McpMeta(ToolMetaKeys.MaxResultSizeChars, ToolMetaKeys.MaxResultSizeCharsValue)]
    [Description(
        "User-visible: shows OneNote's own section picker on the user's screen, so the user can point at a section instead of naming it. It may bring OneNote forward. It doesn't block OneNote: the user can keep working in it while the picker is open. "
        + "Call only when the user asks to choose a place in their notebooks (\"let me pick the section\"); never on your own to find something, which get_hierarchy and find_pages do. "
        + "The picker lists sections only, with one button. It changes no content. "
        + "It waits up to 300 s for the user. A pick returns the section's ID in meta.objectId and its one:Section element as xml (the ID alone if that read fails). Cancel or Esc returns ok with the warning pickerCancelled. "
        + "When 300 s pass with no answer, it returns ok with nothing chosen and the warning pickerLeftOpen: the picker may still be on screen and a choice made in it now is ignored, so ask the user to close it before calling again. "
        + "Only one quick_filing call can wait at a time: another while one waits fails with ComBusy, retryable. "
        + "Showing the picker has a 120 s deadline (the wait for the user doesn't count). A timeout, a lost connection or a cancel while OneNote is showing it returns outcomeUnknown: a picker may be on screen, so ask the user before calling again. A cancel while waiting for the user is Cancelled, retryable, and the picker may stay on screen. "
        + "Success: {ok:true, xml?, meta:{correlationId, objectId?}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> QuickFiling(
        [Description("The picker's title, plain text on one line, at most 255 characters. Omit for \"Choose a section\".")]
        string? title = null,
        [Description("A line under the title saying what the section is for, plain text on one line, at most 1024 characters. Omit for none.")]
        string? description = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, title, description, ct),
            cancellationToken);
}
