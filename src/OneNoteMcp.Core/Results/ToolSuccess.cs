namespace OneNoteMcp.Core.Results;

/// <summary>What a use case returns on success: the <c>ok: true</c> envelope without the <c>ok</c> flag (AD-9).</summary>
public sealed record ToolSuccess(string? Xml, ToolMeta Meta, IReadOnlyList<ToolWarning> Warnings)
{
    public static ToolSuccess WithXml(string xml, ToolMeta meta) => new(xml, meta, []);
}
