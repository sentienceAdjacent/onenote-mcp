namespace OneNoteMcp.Server.Tools;

/// <summary>The <c>_meta</c> every tool declares in <c>tools/list</c> (AD-20).</summary>
public static class ToolMetaKeys
{
    /// <summary>Claude Code's per-tool cap on a result's text before it saves the result to a file and shows a preview.</summary>
    public const string MaxResultSizeChars = "anthropic/maxResultSizeChars";

    /// <summary>
    /// Claude Code's documented ceiling (spike 1.15 addendum). <c>McpMeta</c> has no int overload in SDK 2.3.1; a whole
    /// double serializes as <c>500000</c>, which the contract test checks.
    /// </summary>
    public const double MaxResultSizeCharsValue = 500_000d;

    /// <summary>The sentence every tool that returns a body adds to its description (AD-9, AD-20).</summary>
    public const string LargeResultNote =
        "The text content is one line of the envelope's JSON without the body, then a newline, then the body (the XML, or the base64) unchanged. "
        + "A result too large to return whole returns meta.result {resultId, totalChars, expiresAt, namespaces, pages} and an index instead of the body (meta.basis then has no elementHashes); read the body with read_result. ";
}
