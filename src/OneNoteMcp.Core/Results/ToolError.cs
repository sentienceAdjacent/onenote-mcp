using System.Text.Json.Serialization;

namespace OneNoteMcp.Core.Results;

/// <summary>
/// The envelope's <c>error</c> object (AD-9). <see cref="HResult"/> is formatted like <c>"0x80042014"</c>, and
/// <see cref="OutcomeUnknown"/> is null unless it is true. Both are optional parameters so the output schema
/// doesn't list them as required, since nulls are omitted on the wire. Build one with <see cref="ToolErrors.From"/>.
/// </summary>
public sealed record ToolError(
    [property: JsonPropertyOrder(0)] ErrorCode Code,
    [property: JsonPropertyOrder(2)] bool Retryable,
    [property: JsonPropertyOrder(4)] string UserAction,
    [property: JsonPropertyOrder(5)] string Message,
    [property: JsonPropertyOrder(1), JsonPropertyName("hresult")] string? HResult = null,
    [property: JsonPropertyOrder(3)] bool? OutcomeUnknown = null);
