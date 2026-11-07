using System.Text.Json;
using System.Text.Json.Serialization;
using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Server;

/// <summary>
/// The AD-9 envelope on the wire, and the tools' output schema. Success is <c>{ok:true, xml?, index?, meta, warnings}</c>;
/// failure is <c>{ok:false, error, meta}</c>. camelCase, nulls omitted. It is part of the public contract (AD-14).
/// </summary>
public sealed class ToolEnvelope
{
    [JsonRequired]
    public bool Ok { get; init; }

    public ToolError? Error { get; init; }

    public string? Xml { get; init; }

    /// <summary>An AD-20 index, returned instead of a body that is over the inline threshold, and by <c>read_result</c>.</summary>
    public ResultIndex? Index { get; init; }

    [JsonRequired]
    public required ToolMeta Meta { get; init; }

    public IReadOnlyList<ToolWarning>? Warnings { get; init; }

    public static ToolEnvelope Success(ToolSuccess success)
    {
        ArgumentNullException.ThrowIfNull(success);
        return new() { Ok = true, Xml = success.Xml, Index = success.Index, Meta = success.Meta, Warnings = success.Warnings };
    }

    public static ToolEnvelope Failure(ToolError error, ToolMeta meta) => new() { Ok = false, Error = error, Meta = meta };
}

/// <summary>The one serializer setup for envelopes and tool schemas, so the advertised schema and the payload agree.</summary>
public static class EnvelopeJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
