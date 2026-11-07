using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// Per-call context handed to every use case. Logs (AD-13) and, later, the journal (AD-8) key on <see cref="CorrelationId"/>.
/// <see cref="Results"/> is the call's one read of <c>results{}</c> (AD-20), so sizing, the TTL and the oversize check
/// all use the same values; null when the caller didn't read it. <see cref="Prompt"/> asks the user for a read grant
/// (AD-21); null when the call can't raise the dialog.
/// </summary>
public sealed record ToolCallContext(string CorrelationId, ResultSettings? Results = null, IGrantPrompt? Prompt = null);
