using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// <c>read_result</c>: the server-only tool that reads parts of an oversized result (AD-20). It never calls COM. Thin
/// (AD-5): the policy is in <see cref="ReadResultUseCase"/>.
/// </summary>
[McpServerToolType]
public sealed class ReadResultTool(ToolRunner runner, ReadResultUseCase useCase)
{
    public const string Name = "read_result";

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
        "Read-only. Reads a result that was too large to return whole. When a read tool's result is over the inline limit, it returns meta.result {resultId, totalChars, expiresAt, namespaces, pages} and an index instead of the body; read the body with this tool. It never calls OneNote. "
        + "An index lists the child elements of one element in document order: {ref, element, chars, children, contains?, label?} for one element, or {run: [firstRef, lastRef], count, chars, firstLabel?, lastLabel?} for consecutive siblings small enough to read together. "
        + "A ref is the element's objectID (page XML) or ID (hierarchy XML), unchanged, or <parent ref>/<n> for an element without one; the whole result is /. "
        + "Give exactly one of: ref, to read that element's XML unchanged; run [firstRef, lastRef], to read those siblings together; expand: ref, with page (from 0), to get that element's index; or ref with offset and an optional length, to read characters of that one element's span (markup included) from offset (length defaults to, and is capped at, what fits). "
        + "A ref or run too big to return comes back as its index instead, so a part is never cut. An element with no children but a huge body (a long one:T, one:Data, or a binary result's base64) has an empty index: read it with offset, then offset plus the length returned, until its own chars (from its index entry or its expand response; meta.result.totalChars for /). "
        + "offset takes a single element's ref only. A run returned as an index has index.ref set to the run's parent and index.chars to the run's length, so don't offset-read that ref up to those chars; read the run's members by their own refs. "
        + "Every part is an exact substring of the result; its namespace prefixes are declared in meta.result.namespaces. A page part's meta.basis has the hashes of the Outlines and OEs wholly inside it; an expand of an Outline or OE carries that element's own hash. "
        + "A part inside a password-protected section is served only while the user's read grant that allowed the original call holds; after it ends, ReadNotGranted. "
        + "Results expire ttlMinutes after their last read (30 by default): ResultExpired means repeat the original call. "
        + "Success: {ok:true, xml? (meta.binary for a binary result), index?, meta:{correlationId, result, basis?}, warnings:[]}. "
        + "Failure (isError): {ok:false, error:{code, hresult?, retryable, outcomeUnknown?, userAction, message}, meta:{correlationId}}.")]
    public Task<CallToolResult> ReadResult(
        [Description("The resultId from meta.result.")] string resultId,
        [Description("An element's ref from an index, or / for the whole result. Alone: read that element. With offset: read part of its text.")] string @ref = "",
        [Description("[firstRef, lastRef]: two sibling refs from an index's run entry, read together.")] string[]? run = null,
        [Description("A ref whose index to return: its child elements.")] string expand = "",
        [Description("The index page, from 0, with expand (or a run returned as an index). Defaults to 0.")] int? page = null,
        [Description("With ref: the first character to read, from 0, counted in that element's span (markup included).")] int? offset = null,
        [Description("With offset: how many characters to read. Defaults to, and is capped at, as many as fit.")] int? length = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(
            Name,
            useCase,
            (context, ct) => useCase.ExecuteAsync(context, resultId ?? string.Empty, @ref, run, expand, page, offset, length, ct),
            cancellationToken);
}
