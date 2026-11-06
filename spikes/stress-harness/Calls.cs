using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;

namespace StressHarness;

/// <summary>One tool call through the server, reduced to what the harness compares and records.</summary>
internal sealed record ToolOutcome(
    bool Ok,
    string? Code,
    string? HResult,
    string? Xml,
    JsonElement Meta,
    IReadOnlyList<string> Warnings,
    long Bytes,
    long TextBytes,
    long Ms)
{
    public string? MetaString(string name) =>
        Meta.ValueKind == JsonValueKind.Object && Meta.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public int? MetaInt(string name) =>
        Meta.ValueKind == JsonValueKind.Object && Meta.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

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

    /// <summary>The unreduced result, for the diag modes; null on a protocol failure.</summary>
    public async Task<CallToolResult?> CallRawAsync(string tool, Dictionary<string, object?> args)
    {
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

        sw.Stop();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, McpJsonUtilities.DefaultOptions).LongLength;
        var textBytes = result.Content.OfType<TextContentBlock>().Sum(t => (long)System.Text.Encoding.UTF8.GetByteCount(t.Text));

        JsonElement envelope;
        if (result.StructuredContent is { } sc)
        {
            envelope = JsonSerializer.SerializeToElement(sc, McpJsonUtilities.DefaultOptions);
        }
        else if (result.Content.OfType<TextContentBlock>().FirstOrDefault() is { } text)
        {
            try
            {
                envelope = JsonDocument.Parse(text.Text).RootElement.Clone();
            }
            catch (JsonException)
            {
                return new ToolOutcome(false, "protocol:noEnvelope", null, null, default, [], bytes, textBytes, sw.ElapsedMilliseconds);
            }
        }
        else
        {
            return new ToolOutcome(false, "protocol:noEnvelope", null, null, default, [], bytes, textBytes, sw.ElapsedMilliseconds);
        }

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
        return new ToolOutcome(ok, code, hresult, xml, meta, warnings, bytes, textBytes, sw.ElapsedMilliseconds);
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
