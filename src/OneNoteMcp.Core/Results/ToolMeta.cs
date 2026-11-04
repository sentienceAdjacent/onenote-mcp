namespace OneNoteMcp.Core.Results;

/// <summary>The envelope's <c>meta</c> object. <c>correlationId</c> is on both the success and the error shape (AD-9).</summary>
public sealed record ToolMeta(string CorrelationId);
