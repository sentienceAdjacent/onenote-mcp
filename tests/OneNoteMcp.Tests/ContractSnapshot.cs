using System.Text.Json.Nodes;

namespace OneNoteMcp.Tests;

/// <summary>
/// The tool-contract snapshot (AD-14) and its compact layout. Every tool returns the same envelope, so the file keeps
/// that output schema once, as <c>{contractVersion, envelope: {outputSchema}, tools}</c>: a tool whose
/// <c>outputSchema</c> deep-equals <c>envelope.outputSchema</c> stores <c>"envelope"</c>, any other its full schema.
/// <see cref="Compact"/> writes that layout and <see cref="Expand"/> reads it back, so readers see each tool whole.
/// </summary>
internal static class ContractSnapshot
{
    /// <summary>The marker a tool stores in place of an output schema equal to the envelope's.</summary>
    public const string EnvelopeMarker = "envelope";

    public static string Path => RepoPaths.Combine("tests", "OneNoteMcp.Tests", "Contract", "tool-contract.json");

    /// <summary>The snapshot file as written (compact).</summary>
    public static JsonObject ReadCompact() => JsonNode.Parse(File.ReadAllText(Path))!.AsObject();

    /// <summary>The snapshot file expanded: <c>{contractVersion, tools}</c>, each tool with its full output schema.</summary>
    public static JsonObject Load() => Expand(ReadCompact());

    /// <summary>The expanded tools, ordered by name as the file keeps them.</summary>
    public static JsonArray Tools() => Load()["tools"]!.AsArray();

    /// <summary>
    /// The compact form of an expanded snapshot. The envelope is the most common non-null output schema; on a tie, the
    /// one whose first tool comes first by name (the tools are kept ordered by name).
    /// </summary>
    public static JsonObject Compact(JsonObject expanded)
    {
        ArgumentNullException.ThrowIfNull(expanded);
        var tools = expanded["tools"]!.AsArray().Select(t => t!.AsObject()).ToList();
        var schemas = tools.Select(t => t["outputSchema"]).ToList();

        JsonNode? envelope = null;
        var best = 0;
        foreach (var candidate in schemas.Where(s => s is not null))
        {
            var count = schemas.Count(s => JsonNode.DeepEquals(s, candidate));
            if (count > best)
            {
                best = count;
                envelope = candidate;
            }
        }

        var compactTools = new JsonArray();
        foreach (var tool in tools)
        {
            var copy = new JsonObject();
            foreach (var (key, value) in tool)
            {
                copy[key] = key == "outputSchema" && envelope is not null && JsonNode.DeepEquals(value, envelope)
                    ? JsonValue.Create(EnvelopeMarker)
                    : value?.DeepClone();
            }

            compactTools.Add(copy);
        }

        return new JsonObject
        {
            ["contractVersion"] = expanded["contractVersion"]?.DeepClone(),
            ["envelope"] = new JsonObject { ["outputSchema"] = envelope?.DeepClone() },
            ["tools"] = compactTools,
        };
    }

    /// <summary>The expanded form of a compact snapshot: each <c>"envelope"</c> marker replaced by the envelope's schema.</summary>
    public static JsonObject Expand(JsonObject compact)
    {
        ArgumentNullException.ThrowIfNull(compact);
        var envelope = compact["envelope"]?["outputSchema"];
        var tools = new JsonArray();
        foreach (var tool in compact["tools"]!.AsArray().Select(t => t!.AsObject()))
        {
            var copy = new JsonObject();
            foreach (var (key, value) in tool)
            {
                copy[key] = key == "outputSchema" && value is JsonValue marker && marker.TryGetValue<string>(out var text) && text == EnvelopeMarker
                    ? envelope?.DeepClone() ?? throw new InvalidDataException($"{tool["name"]} names the envelope's output schema, but the snapshot has none.")
                    : value?.DeepClone();
            }

            tools.Add(copy);
        }

        return new JsonObject { ["contractVersion"] = compact["contractVersion"]?.DeepClone(), ["tools"] = tools };
    }
}
