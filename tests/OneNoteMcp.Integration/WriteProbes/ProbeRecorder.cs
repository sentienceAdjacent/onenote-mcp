using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Story 2.2's JSONL probe log, one row per probe, in spike 1's row shape (ts, probe, question, method, args, outcome,
/// verdict, hresult, ms, detail). It writes to <c>%LOCALAPPDATA%\OneNoteMcp-Test\probes\</c>, outside the repo, and
/// every line passes <see cref="Redact"/> first, so a OneDrive account ID is written as <c>&lt;cid&gt;</c>.
/// </summary>
internal sealed partial class ProbeRecorder : IDisposable
{
    // Fields included so tuples serialize; NaN allowed for an absent layout value.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        IncludeFields = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly StreamWriter writer;
    private readonly Action<string> echo;
    private readonly Dictionary<string, SortedSet<string>> hresults = new(StringComparer.Ordinal);

    public ProbeRecorder(string scenario, Action<string> echo)
    {
        Directory.CreateDirectory(ProbesDir);
        Path = System.IO.Path.Combine(ProbesDir, string.Create(CultureInfo.InvariantCulture, $"{scenario}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl"));
        writer = new StreamWriter(Path, append: true) { AutoFlush = true };
        this.echo = echo;
    }

    /// <summary><c>%LOCALAPPDATA%\OneNoteMcp-Test\probes</c>: the raw logs and the hitl pause file.</summary>
    public static string ProbesDir { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneNoteMcp-Test", "probes");

    public string Path { get; }

    /// <summary>Every HRESULT seen so far, with the probes that produced it.</summary>
    public IReadOnlyDictionary<string, SortedSet<string>> HResults => hresults;

    /// <summary>
    /// Replaces a OneDrive account ID with <c>&lt;cid&gt;</c>: in a <c>d.docs.live.net/&lt;cid&gt;/</c> path, a
    /// <c>cid=</c> query value and a <c>&lt;cid&gt;!&lt;n&gt;</c> resource ID. Case-insensitive.
    /// </summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = DocsPath().Replace(text, "${host}<cid>");
        text = CidQuery().Replace(text, "${key}<cid>");
        return ResourceId().Replace(text, "<cid>!");
    }

    public void Row(string probe, string question, string method, object? args, Attempt? attempt, object? detail, string? verdict = null)
    {
        if (attempt?.HResult is { } hr)
        {
            var key = attempt.HResultName is null ? hr : $"{hr} {attempt.HResultName}";
            if (!hresults.TryGetValue(key, out var probes))
            {
                hresults[key] = probes = new SortedSet<string>(StringComparer.Ordinal);
            }

            probes.Add($"{probe} ({method})");
        }

        var row = new Dictionary<string, object?>
        {
            ["ts"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["probe"] = probe,
            ["question"] = question,
            ["method"] = method,
            ["args"] = args,
            ["outcome"] = attempt?.Outcome,
            ["verdict"] = verdict,
            ["hresult"] = attempt?.HResult,
            ["hresultName"] = attempt?.HResultName,
            ["code"] = attempt?.Code,
            ["ms"] = attempt?.Ms,
            ["message"] = attempt?.Message,
            ["detail"] = detail,
        };
        var line = Redact(JsonSerializer.Serialize(row, Json));
        writer.WriteLine(line);
        echo(line);
    }

    public void Dispose() => writer.Dispose();

    [GeneratedRegex(@"(?<host>d\.docs\.live\.net/)[0-9a-f]{8,}(?=/|$|"")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DocsPath();

    [GeneratedRegex(@"(?<key>\bcid=)[0-9a-f]{8,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CidQuery();

    [GeneratedRegex(@"\b[0-9a-f]{16}!(?=\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResourceId();
}

/// <summary>One COM call's result: <c>ok</c>, <c>error</c> (with the HRESULT) or <c>refused</c> (by the guard, before COM).</summary>
internal sealed record Attempt(string Outcome, string? HResult, string? HResultName, string? Code, long Ms, string? Message)
{
    public bool Ok => Outcome == "ok";
}
