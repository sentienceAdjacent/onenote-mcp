using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OneNoteMcp.Server.Tools;

/// <summary>
/// Story 2.5 (AD-9): the argument names a call sent that its tool's input schema doesn't declare, and the
/// <see cref="Core.ErrorCode.InvalidArgument"/> message that names them. Names compare ordinally (case-sensitive), as
/// the SDK binds them. The message echoes the caller's names, cut and capped, and the tool's declared names; neither is
/// ever logged (AD-13).
/// </summary>
public static class ArgumentNames
{
    /// <summary>At most this many unknown names are quoted in the message; the rest are counted.</summary>
    public const int MaxNamed = 10;

    /// <summary>A quoted unknown name is cut to this many characters, then <c>…</c>.</summary>
    public const int MaxNameLength = 64;

    private static readonly JsonSerializerOptions Quoting = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The keys of <paramref name="inputSchema"/>'s <c>properties</c>, in schema order. None when it has none.</summary>
    public static IReadOnlyList<string> Declared(JsonElement inputSchema)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object
            || !inputSchema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return [.. properties.EnumerateObject().Select(p => p.Name)];
    }

    /// <summary>The names in <paramref name="argumentNames"/> that <paramref name="declared"/> doesn't hold, sorted ordinally.</summary>
    public static IReadOnlyList<string> Unknown(IReadOnlyList<string> declared, IEnumerable<string>? argumentNames)
    {
        ArgumentNullException.ThrowIfNull(declared);
        if (argumentNames is null)
        {
            return [];
        }

        var known = declared.ToHashSet(StringComparer.Ordinal);
        return [.. argumentNames.Where(n => !known.Contains(n)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// <c>get_hierarchy doesn't declare the argument "scop". Its arguments are: startNodeId, scope.</c> Every unknown
    /// name is JSON-quoted and cut to <see cref="MaxNameLength"/> characters; past <see cref="MaxNamed"/>, the rest are
    /// counted ("and N more").
    /// </summary>
    public static string Message(string tool, IReadOnlyList<string> declared, IReadOnlyList<string> unknown)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(unknown);
        if (unknown.Count == 0)
        {
            throw new ArgumentException("No unknown argument names to report.", nameof(unknown));
        }

        var text = new StringBuilder();
        text.Append(tool).Append(" doesn't declare the argument").Append(unknown.Count == 1 ? " " : "s ");
        text.AppendJoin(", ", unknown.Take(MaxNamed).Select(Quote));
        if (unknown.Count > MaxNamed)
        {
            text.Append(CultureInfo.InvariantCulture, $" and {unknown.Count - MaxNamed} more");
        }

        text.Append(". ");
        text.Append(declared.Count == 0 ? "It takes no arguments." : "Its arguments are: " + string.Join(", ", declared) + ".");
        return text.ToString();
    }

    private static string Quote(string name)
    {
        if (name.Length > MaxNameLength)
        {
            // Don't split a surrogate pair at the cut.
            var cut = char.IsHighSurrogate(name[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength;
            name = string.Concat(name.AsSpan(0, cut), "…");
        }

        return JsonSerializer.Serialize(name, Quoting);
    }
}
