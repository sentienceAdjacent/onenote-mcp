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

    /// <summary>What every URL and tenant path becomes once <see cref="RedactUrlsFromNowOn"/> is on (spike 3.1, Q5).</summary>
    public const string TenantPlaceholder = "tenant notebook";

    private readonly List<string> tenantPaths = [];

    /// <summary>True once <see cref="RedactUrlsFromNowOn"/> ran: every row and echo line then loses its URLs.</summary>
    public bool RedactsUrls { get; private set; }

    /// <summary>
    /// Spike 3.1's label scenario (Q5): from now on, rows and echo lines replace every <c>http(s)</c> URL, and each of
    /// <paramref name="paths"/> (the tenant notebook's path, in any form), with <see cref="TenantPlaceholder"/>, so the
    /// tenant's SharePoint address and Phill's account never reach the log.
    /// </summary>
    public void RedactUrlsFromNowOn(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        RedactsUrls = true;
        tenantPaths.AddRange(paths.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary><see cref="Redact"/>, then, once URL redaction is on, <see cref="RedactUrls"/>.</summary>
    public string RedactLine(string text) => RedactLine(text, RedactsUrls, tenantPaths);

    /// <summary>The pure form of the instance <see cref="RedactLine(string)"/> (unit-tested without writing a log).</summary>
    internal static string RedactLine(string text, bool urls, IEnumerable<string> paths) => Redact(urls ? RedactUrls(text, paths) : text); // URLs first: "<cid>" would end one early

    /// <summary>
    /// Replaces every <c>http(s)</c> URL, then each of <paramref name="knownPaths"/> in any other form (without its
    /// trailing separator, as written and JSON-escaped), with <see cref="TenantPlaceholder"/>. A URL runs to the next
    /// quote, backslash (bar a JSON <c>\uXXXX</c> or <c>\/</c> escape), angle bracket or line end, so a JSON string value
    /// loses the whole URL, spaces included, and stays valid JSON; in free text the rest of the line goes with it.
    /// </summary>
    public static string RedactUrls(string text, IEnumerable<string> knownPaths)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(knownPaths);
        text = Url().Replace(text, TenantPlaceholder);
        var forms = knownPaths
            .Select(p => p.TrimEnd('/', '\\'))
            .SelectMany(p => new[] { p, JsonSerializer.Serialize(p, Json)[1..^1] })
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p.Length);
        foreach (var form in forms)
        {
            text = text.Replace(form, TenantPlaceholder, StringComparison.OrdinalIgnoreCase);
        }

        return text;
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
        var line = RedactLine(JsonSerializer.Serialize(row, Json));
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

    [GeneratedRegex(@"https?://(?:[^""\\\r\n<>]|\\u[0-9a-fA-F]{4}|\\/)*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Url();
}

/// <summary>One COM call's result: <c>ok</c>, <c>error</c> (with the HRESULT) or <c>refused</c> (by the guard, before COM).</summary>
internal sealed record Attempt(string Outcome, string? HResult, string? HResultName, string? Code, long Ms, string? Message)
{
    public bool Ok => Outcome == "ok";
}
