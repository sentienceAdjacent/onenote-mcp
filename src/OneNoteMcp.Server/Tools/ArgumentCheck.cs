using System.Text.Json;

namespace OneNoteMcp.Server.Tools;

/// <summary>A required argument the call left out, or a value that doesn't fit its property's schema.</summary>
/// <param name="Name">The declared argument's name (the tool's, never the caller's).</param>
/// <param name="Expected">What the schema takes, e.g. <c>a string</c>, <c>an integer or null</c>, <c>one of: a, b</c>.</param>
/// <param name="Got">The JSON kind the call sent, e.g. <c>a number</c>; never the value. Null for a missing argument.</param>
public sealed record ArgumentProblem(string Name, string Expected, string? Got);

/// <summary>Everything wrong with one call's arguments, in message order.</summary>
public sealed record ArgumentProblems(
    IReadOnlyList<string> Declared,
    IReadOnlyList<ArgumentProblem> Missing,
    IReadOnlyList<ArgumentProblem> Invalid,
    IReadOnlyList<string> Unknown)
{
    public bool Any => Missing.Count > 0 || Invalid.Count > 0 || Unknown.Count > 0;
}

/// <summary>
/// Story 2.14 (AD-9): a call's arguments checked against its tool's input schema before the SDK binds them, and the
/// <see cref="Core.ErrorCode.InvalidArgument"/> message that names every bad argument and what it expects. The check is
/// strict: <c>string</c> is a JSON string, <c>boolean</c> is true or false, <c>integer</c> is a JSON number that fits
/// an <see cref="int"/>, <c>null</c> only where the type lists it, <c>array</c> checks each item against <c>items</c>,
/// <c>object</c> checks each member against <c>properties</c> or <c>additionalProperties</c> (story 2.9), and
/// <c>enum</c> compares ordinally. A name the schema doesn't declare is always refused (story 2.5), whatever
/// <c>additionalProperties</c> says. The message never holds a caller's value, only its JSON kind (AD-9, AD-13).
/// </summary>
public static class ArgumentCheck
{
    /// <summary>What's wrong with <paramref name="arguments"/> against <paramref name="inputSchema"/>; nothing when they fit.</summary>
    public static ArgumentProblems Problems(JsonElement inputSchema, IDictionary<string, JsonElement>? arguments)
    {
        var declared = ArgumentNames.Declared(inputSchema);
        var properties = inputSchema.ValueKind == JsonValueKind.Object
            && inputSchema.TryGetProperty("properties", out var p)
            && p.ValueKind == JsonValueKind.Object
                ? p
                : default;

        var missing = new List<ArgumentProblem>();
        if (inputSchema.ValueKind == JsonValueKind.Object
            && inputSchema.TryGetProperty("required", out var required)
            && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!))
            {
                if (arguments is null || !arguments.ContainsKey(name))
                {
                    missing.Add(new ArgumentProblem(name, PropertySchema(properties, name) is { } schema ? Expected(schema) : string.Empty, null));
                }
            }
        }

        var invalid = new List<ArgumentProblem>();
        if (arguments is not null && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (arguments.TryGetValue(property.Name, out var value) && Mismatch(property.Value, value) is { } got)
                {
                    invalid.Add(new ArgumentProblem(property.Name, Expected(property.Value), got));
                }
            }
        }

        return new ArgumentProblems(declared, missing, invalid, ArgumentNames.Unknown(declared, arguments?.Keys));
    }

    /// <summary>
    /// One sentence per missing argument, then one per invalid one (schema order), then the unknown names, then the
    /// declared arguments. Each sentence starts with <paramref name="tool"/>. With only unknown names it is story 2.5's
    /// <see cref="ArgumentNames.Message"/> exactly.
    /// </summary>
    public static string Message(string tool, ArgumentProblems problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        if (!problems.Any)
        {
            throw new ArgumentException("No argument problems to report.", nameof(problems));
        }

        var sentences = new List<string>();
        sentences.AddRange(problems.Missing.Select(m => m.Expected.Length == 0
            ? $"{tool} is missing the required argument {ArgumentNames.Quote(m.Name)}."
            : $"{tool} is missing the required argument {ArgumentNames.Quote(m.Name)} ({m.Expected})."));
        sentences.AddRange(problems.Invalid.Select(i => $"{tool}'s argument {ArgumentNames.Quote(i.Name)} must be {i.Expected} (it got {i.Got})."));
        if (problems.Unknown.Count > 0)
        {
            sentences.Add(ArgumentNames.UnknownSentence(tool, problems.Unknown));
        }

        sentences.Add(ArgumentNames.Trailer(problems.Declared));
        return string.Join(" ", sentences);
    }

    /// <summary>The <see cref="Core.ErrorCode.InvalidArgument"/> message for this call, or null when its arguments fit.</summary>
    public static string? Check(string tool, JsonElement inputSchema, IDictionary<string, JsonElement>? arguments)
    {
        var problems = Problems(inputSchema, arguments);
        return problems.Any ? Message(tool, problems) : null;
    }

    /// <summary>What a property's schema takes, in words: <c>a string</c>, <c>an integer or null</c>, <c>one of: a, b</c>.</summary>
    public static string Expected(JsonElement schema)
    {
        if (Enum(schema) is { } values)
        {
            return "one of: " + string.Join(", ", values.Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()));
        }

        var types = Types(schema);
        return types.Count == 0 ? "any value" : JoinOr(types.Select(t => Singular(t, schema)));
    }

    /// <summary>The JSON kind <paramref name="value"/> has when it doesn't fit <paramref name="schema"/>; null when it fits.</summary>
    public static string? Mismatch(JsonElement schema, JsonElement value)
    {
        var types = Types(schema);
        if (types.Count > 0)
        {
            if (!types.Any(t => Accepts(t, value)))
            {
                return value.ValueKind == JsonValueKind.Number && types.Contains("integer") && !types.Contains("number")
                    ? "a number that isn't a 32-bit integer"
                    : Kind(value);
            }

            if (value.ValueKind == JsonValueKind.Array
                && schema.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (Mismatch(items, item) is { } itemGot)
                    {
                        return "an array with an item that is " + itemGot;
                    }
                }
            }

            if (value.ValueKind == JsonValueKind.Object && MemberMismatch(schema, value) is { } memberGot)
            {
                return memberGot;
            }
        }

        if (Enum(schema) is { } values && !values.Any(v => SameValue(v, value)))
        {
            return Kind(value) + " that isn't one of them";
        }

        return null;
    }

    /// <summary>
    /// An object argument's members (story 2.9, <c>update_page_content</c>'s <c>basis</c>): each declared member against
    /// its <c>properties</c> schema, each other member against <c>additionalProperties</c> (refused when that is
    /// <c>false</c>). The result names neither the member nor its value, only the JSON kind (AD-9, AD-13).
    /// </summary>
    private static string? MemberMismatch(JsonElement schema, JsonElement value)
    {
        var properties = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        var additional = schema.TryGetProperty("additionalProperties", out var a) ? a : default;
        foreach (var member in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(member.Name, out var memberSchema))
            {
                if (Mismatch(memberSchema, member.Value) is { } got)
                {
                    return "an object with a member that is " + got;
                }
            }
            else if (additional.ValueKind == JsonValueKind.False)
            {
                return "an object with a member it doesn't declare";
            }
            else if (additional.ValueKind == JsonValueKind.Object && Mismatch(additional, member.Value) is { } got)
            {
                return "an object with a member that is " + got;
            }
        }

        return null;
    }

    private static JsonElement? PropertySchema(JsonElement properties, string name) =>
        properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var schema) ? schema : null;

    private static List<string> Types(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type))
        {
            return [];
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => [type.GetString()!],
            JsonValueKind.Array => [.. type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!)],
            _ => [],
        };
    }

    private static List<JsonElement>? Enum(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array
            ? [.. values.EnumerateArray()]
            : null;

    private static bool Accepts(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "null" => value.ValueKind == JsonValueKind.Null,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        _ => true, // a type keyword this check doesn't know constrains nothing here
    };

    private static bool SameValue(JsonElement allowed, JsonElement value) =>
        allowed.ValueKind == JsonValueKind.String && value.ValueKind == JsonValueKind.String
            ? string.Equals(allowed.GetString(), value.GetString(), StringComparison.Ordinal)
            : JsonElement.DeepEquals(allowed, value);

    private static string Singular(string type, JsonElement schema) => type switch
    {
        "string" => "a string",
        "boolean" => "true or false",
        "integer" => "an integer",
        "number" => "a number",
        "null" => "null",
        "object" => "an object",
        "array" => ArrayOf(schema),
        _ => type,
    };

    private static string ArrayOf(JsonElement schema)
    {
        if (!schema.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
        {
            return "an array";
        }

        if (Enum(items) is { } values)
        {
            return "an array of values from: " + string.Join(", ", values.Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()));
        }

        var types = Types(items);
        return types.Count == 0 ? "an array" : "an array of " + JoinOr(types.Select(t => Plural(t, items)));
    }

    private static string Plural(string type, JsonElement schema) => type switch
    {
        "string" => "strings",
        "boolean" => "booleans",
        "integer" => "integers",
        "number" => "numbers",
        "null" => "nulls",
        "object" => "objects",
        "array" => "arrays",
        _ => type,
    };

    /// <summary><c>a or b</c>; <c>a, or b</c> when a part already says "or" (<c>an array of strings or nulls, or null</c>).</summary>
    private static string JoinOr(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        return string.Join(list.Any(p => p.Contains(" or ", StringComparison.Ordinal)) ? ", or " : " or ", list);
    }

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => "no value",
    };
}
