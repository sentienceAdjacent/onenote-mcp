namespace OneNoteMcp.Core.UseCases;

/// <summary>Per-call context handed to every use case. Logs (AD-13) and, later, the journal (AD-8) key on <see cref="CorrelationId"/>.</summary>
public sealed record ToolCallContext(string CorrelationId);
