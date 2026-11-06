using System.Text;
using System.Text.Json;

namespace StressHarness;

/// <summary>Match states. Only <see cref="Mismatch"/> counts as a failure.</summary>
internal static class Match
{
    public const string Same = "match";
    public const string Mismatch = "mismatch";

    /// <summary>The two raw reads differed: OneNote changed under the run (Phill may be editing).</summary>
    public const string ChangedDuringRun = "changedDuringRun";

    /// <summary>The first tool read differed, the raw reads agreed, and the retried tool read matched them.</summary>
    public const string Recovered = "recovered";

    /// <summary>
    /// Two back-to-back raw reads differed with nothing in between: OneNote itself returns unstable XML for the object
    /// (not an edit or sync, which is <see cref="ChangedDuringRun"/>; not a server fault, which is <see cref="Mismatch"/>).
    /// </summary>
    public const string UnstableRead = "unstableRead";

    /// <summary>Nothing to compare (for example, a search whose prediction depends on an unreadable page).</summary>
    public const string NotApplicable = "n/a";
}

/// <summary>
/// One line of run.jsonl. Only IDs, numbers, tool and error codes, HRESULTs, exception type names and fixed labels
/// go in; there is deliberately no way to add free text.
/// </summary>
internal sealed class CallRecord
{
    public required string Phase { get; init; }

    public required string Tool { get; init; }

    /// <summary>The tool plus its variant (scope, includeBinary), for per-kind stats.</summary>
    public required string Kind { get; init; }

    public string? TargetId { get; init; }

    public string? Match { get; set; }

    public ToolOutcome? ToolResult { get; set; }

    public RawOutcome? RawResult { get; set; }

    public bool Retried { get; set; }

    public Dictionary<string, long> Counts { get; } = [];

    /// <summary>Schema element and attribute names only (an element path such as one:Page/one:Outline/...).</summary>
    public Dictionary<string, string> Labels { get; } = [];
}

internal sealed class Recorder : IDisposable
{
    public const int CapTokens = 25_000;
    public const long CallTimeoutMs = 30_000;

    private readonly Utf8JsonWriter _writer;
    private readonly Stream _stream;
    private readonly List<CallRecord> _records = [];

    public Recorder(string outDir)
    {
        OutDir = outDir;
        _stream = File.Create(Path.Combine(outDir, "run.jsonl"));
        _writer = new Utf8JsonWriter(_stream, new JsonWriterOptions { Indented = false });
    }

    public string OutDir { get; }

    public Dictionary<string, long> NotebookCounts { get; } = [];

    /// <summary>Version strings, a hash and a timestamp only.</summary>
    public Dictionary<string, string> Build { get; } = [];

    public IReadOnlyList<CallRecord> Records => _records;

    public void Add(CallRecord r)
    {
        _records.Add(r);
        _writer.Reset();
        _writer.WriteStartObject();
        _writer.WriteString("phase", r.Phase);
        _writer.WriteString("tool", r.Tool);
        _writer.WriteString("kind", r.Kind);
        if (r.TargetId is not null)
        {
            _writer.WriteString("targetId", r.TargetId);
        }

        _writer.WriteString("match", r.Match ?? StressHarness.Match.NotApplicable);
        _writer.WriteBoolean("retried", r.Retried);
        if (r.ToolResult is { } t)
        {
            _writer.WriteBoolean("ok", t.Ok);
            if (t.Code is not null)
            {
                _writer.WriteString("code", t.Code);
            }

            if (t.HResult is not null)
            {
                _writer.WriteString("hresult", t.HResult);
            }

            _writer.WriteNumber("bytes", t.Bytes);
            _writer.WriteNumber("textBytes", t.TextBytes);
            _writer.WriteNumber("ms", t.Ms);
            _writer.WriteStartArray("warnings");
            foreach (var w in t.Warnings)
            {
                _writer.WriteStringValue(w);
            }

            _writer.WriteEndArray();
        }

        if (r.RawResult is { } raw)
        {
            _writer.WriteBoolean("rawOk", raw.Ok);
            if (raw.Code is not null)
            {
                _writer.WriteString("rawCode", raw.Code);
            }

            if (raw.HResultText is not null)
            {
                _writer.WriteString("rawHresult", raw.HResultText);
            }

            _writer.WriteNumber("rawMs", raw.Ms);
            if (raw.Value is not null)
            {
                _writer.WriteNumber("rawChars", raw.Value.Length);
            }
        }

        foreach (var (k, v) in r.Counts)
        {
            _writer.WriteNumber(k, v);
        }

        foreach (var (k, v) in r.Labels)
        {
            _writer.WriteString(k, v);
        }

        _writer.WriteEndObject();
        _writer.Flush();
        _stream.WriteByte((byte)'\n');
        _stream.Flush();

        // Keep only the numbers for the summary; the XML and binaries are dropped here.
        r.ToolResult = r.ToolResult is { } tr ? tr with { Xml = null, Meta = default } : null;
        r.RawResult = r.RawResult is { } rr ? rr with { Value = null } : null;
    }

    public void WriteSummary(string notebookId, Options o, long elapsedMs, string? abortedBy)
    {
        using var stream = File.Create(Path.Combine(OutDir, "summary.json"));
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        var withTool = _records.Where(r => r.ToolResult is not null).ToList();

        w.WriteStartObject();
        w.WriteString("harness", "spike-1.15-stress-harness");
        w.WriteString("notebookId", notebookId);
        w.WriteNumber("rng", o.Seed); // not "seed": a grep for the Seed section name must find nothing
        w.WriteNumber("elapsedMs", elapsedMs);

        // Which builds were compared: the server under test and the oracle can differ.
        w.WriteStartObject("build");
        foreach (var (k, v) in Build)
        {
            w.WriteString(k, v);
        }

        w.WriteEndObject();
        if (abortedBy is not null)
        {
            w.WriteString("abortedBy", abortedBy);
        }

        w.WriteStartObject("notebook");
        foreach (var (k, v) in NotebookCounts)
        {
            w.WriteNumber(k, v);
        }

        w.WriteEndObject();

        w.WriteNumber("calls", withTool.Count);
        w.WriteNumber("mismatches", _records.Count(r => r.Match == StressHarness.Match.Mismatch));
        WriteCountBy(w, "matchStates", _records.GroupBy(r => r.Match ?? StressHarness.Match.NotApplicable));
        w.WriteStartObject("byPhase");
        foreach (var g in _records.GroupBy(r => r.Phase))
        {
            WriteCountBy(w, g.Key, g.GroupBy(r => r.Match ?? StressHarness.Match.NotApplicable));
        }

        w.WriteEndObject();
        WriteCountBy(w, "errorsByCode", withTool.Where(r => !r.ToolResult!.Ok).GroupBy(r => r.Tool + ":" + r.ToolResult!.Code));
        WriteCountBy(w, "warningsByCode", withTool.SelectMany(r => r.ToolResult!.Warnings.Select(c => (r.Tool, c))).GroupBy(x => x.Tool + ":" + x.c));

        w.WriteStartObject("byTool");
        foreach (var g in withTool.GroupBy(r => r.Tool).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            WriteStats(w, g.Key, g.ToList(), top: true);
        }

        w.WriteEndObject();
        w.WriteStartObject("byKind");
        foreach (var g in withTool.GroupBy(r => r.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            WriteStats(w, g.Key, g.ToList(), top: false);
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteStats(Utf8JsonWriter w, string name, List<CallRecord> rs, bool top)
    {
        var bytes = rs.Select(r => r.ToolResult!.Bytes).Order().ToList();
        var ms = rs.Select(r => r.ToolResult!.Ms).Order().ToList();
        var raw = rs.Where(r => r.RawResult is not null).Select(r => r.RawResult!.Ms).Order().ToList();
        w.WriteStartObject(name);
        w.WriteNumber("calls", rs.Count);
        w.WriteNumber("ok", rs.Count(r => r.ToolResult!.Ok));
        w.WriteNumber("mismatches", rs.Count(r => r.Match == StressHarness.Match.Mismatch));
        Percentiles(w, "bytes", bytes);
        // roundtripMs is the whole tool call (it can span many COM calls); rawMs is one COM call, which is what the
        // 30 s timeouts.call bounds, so the timeout counts below use rawMs.
        Percentiles(w, "roundtripMs", ms);
        Percentiles(w, "rawMs", raw);
        w.WriteNumber("over25kTokensAtBytesPer4", bytes.Count(b => b / 4 > CapTokens));
        w.WriteNumber("over25kTokensAtBytesPer3", bytes.Count(b => b / 3 > CapTokens));

        // The text block is what Claude Code hands the model (structuredContent repeats it), so it's what the cap meets.
        var text = rs.Select(r => r.ToolResult!.TextBytes).Order().ToList();
        Percentiles(w, "textBytes", text);
        w.WriteNumber("textOver25kTokensAtBytesPer4", text.Count(b => b / 4 > CapTokens));
        w.WriteNumber("textOver25kTokensAtBytesPer3", text.Count(b => b / 3 > CapTokens));

        // Claude Code 2.1.318 persists any result over ~50,000 characters and shows the model a 2 KB preview (cap probe).
        w.WriteNumber("textOver50kBytes", text.Count(b => b > 50_000));
        w.WriteNumber("rawOver50PctCallTimeout", raw.Count(m => m > CallTimeoutMs / 2));
        w.WriteNumber("rawOver100PctCallTimeout", raw.Count(m => m > CallTimeoutMs));
        if (top)
        {
            w.WriteStartArray("top10Bytes");
            foreach (var r in rs.OrderByDescending(r => r.ToolResult!.Bytes).Take(10))
            {
                w.WriteStartObject();
                w.WriteString("kind", r.Kind);
                if (r.TargetId is not null)
                {
                    w.WriteString("targetId", r.TargetId);
                }

                w.WriteNumber("bytes", r.ToolResult!.Bytes);
                w.WriteNumber("textBytes", r.ToolResult!.TextBytes);
                w.WriteNumber("ms", r.ToolResult!.Ms);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void Percentiles(Utf8JsonWriter w, string name, List<long> sorted)
    {
        w.WriteStartObject(name);
        if (sorted.Count > 0)
        {
            w.WriteNumber("p50", At(sorted, 0.50));
            w.WriteNumber("p95", At(sorted, 0.95));
            w.WriteNumber("max", sorted[^1]);
            w.WriteNumber("total", sorted.Sum());
        }

        w.WriteEndObject();
    }

    // Nearest rank.
    private static long At(List<long> sorted, double q) => sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static void WriteCountBy<T>(Utf8JsonWriter w, string name, IEnumerable<IGrouping<string, T>> groups)
    {
        w.WriteStartObject(name);
        foreach (var g in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            w.WriteNumber(g.Key, g.Count());
        }

        w.WriteEndObject();
    }

    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();
    }
}

/// <summary>Progress on stderr: phase names and counts only.</summary>
internal static class Progress
{
    private static readonly StringBuilder Line = new();

    public static void Report(string phase, int done, int total)
    {
        if (done == total || done % 25 == 0)
        {
            Line.Clear().Append('[').Append(phase).Append("] ").Append(done).Append('/').Append(total);
            Console.Error.WriteLine(Line.ToString());
        }
    }

    public static void Say(string fixedText) => Console.Error.WriteLine(fixedText);
}
