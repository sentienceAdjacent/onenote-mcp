using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;

namespace StressHarness;

/// <summary>
/// One tool call through the server, reduced to what the harness compares and records. A result over the server's
/// inline threshold (AD-20) is reassembled through <c>read_result</c> first, so <see cref="Xml"/> (or
/// <c>meta.binary</c>) is always the whole body. <see cref="TextChars"/> is the original call's text block;
/// <see cref="MaxTextChars"/> is the largest text block of the call and its <c>read_result</c> parts.
/// </summary>
internal sealed record ToolOutcome(
    bool Ok,
    string? Code,
    string? HResult,
    string? Xml,
    JsonElement Meta,
    IReadOnlyList<string> Warnings,
    long Bytes,
    long TextBytes,
    long Ms,
    int TextChars = 0,
    int Parts = 0,
    int MaxTextChars = 0)
{
    /// <summary>True when the server stored the result and the harness read it back through <c>read_result</c>.</summary>
    public bool Stored => Parts > 0;

    public string? MetaString(string name) =>
        Meta.ValueKind == JsonValueKind.Object && Meta.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public int? MetaInt(string name) =>
        Meta.ValueKind == JsonValueKind.Object && Meta.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    /// <summary>A string-array meta field, such as <c>withheld</c> (AD-21); empty when absent.</summary>
    public IReadOnlyList<string> MetaStrings(string name) =>
        Meta.ValueKind == JsonValueKind.Object && Meta.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(e => e.GetString() ?? string.Empty)]
            : [];

    public int FilteredCount(string reason) =>
        Meta.ValueKind == JsonValueKind.Object && Meta.TryGetProperty("filtered", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Count(e => e.TryGetProperty("reason", out var r) && r.GetString() == reason)
            : 0;
}

/// <summary>One raw read through ComGateway. <see cref="Key"/> is what "the two raw reads differ" compares.</summary>
internal sealed record RawOutcome(bool Ok, string? Code, int? HResult, string? Value, long Ms)
{
    public string Key => Ok ? "ok:" + Value : "err:" + Code + ":" + HResultText;

    public string? HResultText => HResult is { } h ? "0x" + h.ToString("X8", CultureInfo.InvariantCulture) : null;
}

internal sealed class ToolClient(McpClient client)
{
    // A tool can make many COM calls (find_pages reads every candidate page), so the harness's own wait is generous.
    private static readonly TimeSpan CallLimit = TimeSpan.FromMinutes(15);

    /// <summary>The user-visible tool (story 1.21, AD-11). The harness never calls it: every call path refuses it.</summary>
    public const string NavigateTo = "navigate_to";

    /// <summary>The write tools (story 2.4 on). The harness never calls them either: it only reads.</summary>
    public static readonly IReadOnlySet<string> WriteTools = new HashSet<string>(StringComparer.Ordinal) { "create_page" };

    /// <summary>The tools that may lack readOnlyHint: the user-visible one and the writes. Every other contract tool must be read-only.</summary>
    public static bool MayWrite(string tool) => string.Equals(tool, NavigateTo, StringComparison.Ordinal) || WriteTools.Contains(tool);

    /// <summary>The unreduced result, for the diag modes; null on a protocol failure.</summary>
    public async Task<CallToolResult?> CallRawAsync(string tool, Dictionary<string, object?> args)
    {
        RefuseUserVisible(tool);
        using var cts = new CancellationTokenSource(CallLimit);
        try
        {
            return await client.CallToolAsync(tool, args, cancellationToken: cts.Token);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<ToolOutcome> CallAsync(string tool, Dictionary<string, object?> args)
    {
        RefuseUserVisible(tool);
        using var cts = new CancellationTokenSource(CallLimit);
        var sw = Stopwatch.StartNew();
        CallToolResult result;
        try
        {
            result = await client.CallToolAsync(tool, args, cancellationToken: cts.Token);
        }
        catch (Exception ex)
        {
            // A protocol-level failure, not an envelope. Only the exception's type name is kept.
            sw.Stop();
            return new ToolOutcome(false, "protocol:" + ex.GetType().Name, null, null, default, [], 0, 0, sw.ElapsedMilliseconds);
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, McpJsonUtilities.DefaultOptions).LongLength;
        var textBytes = result.Content.OfType<TextContentBlock>().Sum(t => (long)System.Text.Encoding.UTF8.GetByteCount(t.Text));
        var textChars = TextChars(result);

        if (Envelope(result, BodyIsBinary(tool)) is not { } parsed)
        {
            sw.Stop();
            return new ToolOutcome(false, "protocol:noEnvelope", null, null, default, [], bytes, textBytes, sw.ElapsedMilliseconds, textChars, 0, textChars);
        }

        var envelope = parsed;
        var parts = 0;
        var maxText = textChars;
        if (IsStored(envelope))
        {
            // AD-20: the body was too large to return whole. Reassemble it by offset reads of "/" so the comparison
            // with the raw COM read is over the whole body, exactly as the tool would have returned it inline.
            var reassembled = await ReassembleAsync(tool, envelope);
            if (reassembled.Code is { } failed)
            {
                sw.Stop();
                return new ToolOutcome(false, failed, null, null, default, [], bytes, textBytes, sw.ElapsedMilliseconds, textChars, reassembled.Parts, Math.Max(maxText, reassembled.MaxText));
            }

            envelope = reassembled.Envelope;
            parts = reassembled.Parts;
            maxText = Math.Max(maxText, reassembled.MaxText);
        }

        sw.Stop();

        var ok = envelope.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
        string? code = null, hresult = null;
        if (envelope.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            code = err.TryGetProperty("code", out var c) ? c.GetString() : null;
            hresult = err.TryGetProperty("hresult", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
        }

        var xml = envelope.TryGetProperty("xml", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
        var meta = envelope.TryGetProperty("meta", out var m) ? m.Clone() : default;
        var warnings = envelope.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(e => e.TryGetProperty("code", out var wc) ? wc.GetString() ?? "" : "").ToList()
            : [];
        return new ToolOutcome(ok, code, hresult, xml, meta, warnings, bytes, textBytes, sw.ElapsedMilliseconds, textChars, parts, maxText);
    }

    private static bool BodyIsBinary(string tool) => tool == "get_binary_page_content";

    private static int TextChars(CallToolResult result) => result.Content.OfType<TextContentBlock>().Sum(t => t.Text.Length);

    /// <summary>
    /// The envelope: structuredContent, else the text block. AD-9's text block is the envelope JSON, or for a success
    /// with a body, a header line of JSON without the body, then a newline and the body.
    /// </summary>
    private static JsonElement? Envelope(CallToolResult result, bool bodyIsBinary)
    {
        if (result.StructuredContent is { } sc)
        {
            return JsonSerializer.SerializeToElement(sc, McpJsonUtilities.DefaultOptions);
        }

        if (result.Content.OfType<TextContentBlock>().FirstOrDefault() is not { } text)
        {
            return null;
        }

        var newline = text.Text.IndexOf('\n', StringComparison.Ordinal);
        try
        {
            if (newline < 0)
            {
                return JsonDocument.Parse(text.Text).RootElement.Clone();
            }

            var header = JsonNode.Parse(text.Text[..newline])!.AsObject();
            var body = text.Text[(newline + 1)..];
            if (bodyIsBinary)
            {
                header["meta"]!.AsObject()["binary"] = body;
            }
            else
            {
                header["xml"] = body;
            }

            return JsonSerializer.SerializeToElement(header);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsStored(JsonElement envelope) =>
        envelope.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
        && envelope.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Object
        && envelope.TryGetProperty("meta", out var meta) && meta.TryGetProperty("result", out _);

    private sealed record Reassembly(JsonElement Envelope, int Parts, int MaxText, string? Code);

    private async Task<Reassembly> ReassembleAsync(string tool, JsonElement envelope)
    {
        var result = envelope.GetProperty("meta").GetProperty("result");
        var resultId = result.GetProperty("resultId").GetString()!;
        var total = result.GetProperty("totalChars").GetInt32();
        var binary = BodyIsBinary(tool);
        var body = new System.Text.StringBuilder(total);
        var parts = 0;
        var maxText = 0;
        while (body.Length < total)
        {
            CallToolResult part;
            using (var cts = new CancellationTokenSource(CallLimit))
            {
                try
                {
                    part = await client.CallToolAsync(
                        "read_result",
                        new Dictionary<string, object?> { ["resultId"] = resultId, ["ref"] = "/", ["offset"] = body.Length },
                        cancellationToken: cts.Token);
                }
                catch (Exception ex)
                {
                    return new Reassembly(envelope, parts, maxText, "reassembly:protocol:" + ex.GetType().Name);
                }
            }

            parts++;
            maxText = Math.Max(maxText, TextChars(part));
            var p = Envelope(part, binary);
            if (p is not { } env || !env.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var code = p is { } e && e.TryGetProperty("error", out var err) && err.TryGetProperty("code", out var c) ? c.GetString() : "noEnvelope";
                return new Reassembly(envelope, parts, maxText, "reassembly:" + code);
            }

            var chunk = binary
                ? env.GetProperty("meta").TryGetProperty("binary", out var b) ? b.GetString() : null
                : env.TryGetProperty("xml", out var x) ? x.GetString() : null;
            if (string.IsNullOrEmpty(chunk))
            {
                return new Reassembly(envelope, parts, maxText, "reassembly:emptyPart");
            }

            body.Append(chunk);
        }

        // The original envelope, with the body put back where an inline result has it and the AD-20 fields kept.
        var node = JsonNode.Parse(envelope.GetRawText())!.AsObject();
        if (binary)
        {
            node["meta"]!.AsObject()["binary"] = body.ToString();
        }
        else
        {
            node["xml"] = body.ToString();
        }

        return new Reassembly(JsonSerializer.SerializeToElement(node), parts, maxText, null);
    }

    // A guard against a future phase calling one by mistake; outside each call's try, so it stops the run.
    private static void RefuseUserVisible(string tool)
    {
        if (string.Equals(tool, NavigateTo, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the harness never calls navigate_to (AD-11)");
        }

        if (WriteTools.Contains(tool))
        {
            throw new InvalidOperationException("the harness never calls a write tool (AD-7)");
        }
    }
}

internal static class Raw
{
    public static async Task<RawOutcome> RunAsync(Func<CancellationToken, Task<string>> read)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var sw = Stopwatch.StartNew();
        try
        {
            var value = await read(cts.Token);
            sw.Stop();
            return new RawOutcome(true, null, null, value ?? string.Empty, sw.ElapsedMilliseconds);
        }
        catch (OneNoteException ex)
        {
            sw.Stop();
            return new RawOutcome(false, ex.Code.ToString(), ex.ComHResult, null, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new RawOutcome(false, "exception:" + ex.GetType().Name, ex.HResult, null, sw.ElapsedMilliseconds);
        }
    }
}
