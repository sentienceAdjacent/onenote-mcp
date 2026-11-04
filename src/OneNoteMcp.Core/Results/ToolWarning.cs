namespace OneNoteMcp.Core.Results;

/// <summary>A degraded but successful detail, such as <c>BinaryUnavailable</c> (AD-4, AD-9). Warnings never carry page content.</summary>
public sealed record ToolWarning(string Code, string Message, string? UserAction = null);
