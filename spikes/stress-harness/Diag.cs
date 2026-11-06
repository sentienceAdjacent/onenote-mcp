using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Com;
using OneNoteMcp.Core;

namespace StressHarness;

/// <summary>
/// Diagnostic modes. Content rule as for the harness: they print only lengths, offsets, counts, U+hex codepoints,
/// schema element and attribute names, codes and IDs. Never text, titles or names.
/// </summary>
internal static class Diag
{
    private const int BinaryObjectDoesNotExist = unchecked((int)0x8004200F);

    /// <summary>One page's get_page_content (includeBinary false) against raw piBasic.</summary>
    public static async Task<int> PageAsync(ComGateway gateway, ToolClient tools, string pageId)
    {
        // Order experiment: STRESS_TOOL_FIRST=1 calls the tool before the raw read.
        var toolFirst = Environment.GetEnvironmentVariable("STRESS_TOOL_FIRST") == "1";
        CallToolResult? result = null;
        if (toolFirst)
        {
            result = await tools.CallRawAsync("get_page_content", new() { ["pageId"] = pageId, ["includeBinary"] = false });
        }

        var raw = await Raw.RunAsync(ct => gateway.GetPageContentAsync(pageId, PageInfo.Basic, ct));
        if (!toolFirst)
        {
            result = await tools.CallRawAsync("get_page_content", new() { ["pageId"] = pageId, ["includeBinary"] = false });
        }

        Console.WriteLine($"order: {(toolFirst ? "tool then raw" : "raw then tool")}");

        // A second COM connection in this same process, opened after both reads: which value does it get?
        var settings = Path.Combine(Path.GetTempPath(), "onenote-mcp-stress", Guid.NewGuid().ToString("N"), "settings.json");
        using (var second = new ComGateway(new ProcessProbe(), new OneNoteMcp.Core.Settings.SettingsStore(settings)))
        {
            var again = await Raw.RunAsync(ct => second.GetPageContentAsync(pageId, PageInfo.Basic, ct));
            var first = await Raw.RunAsync(ct => gateway.GetPageContentAsync(pageId, PageInfo.Basic, ct));
            var toolXml = result?.StructuredContent is { } s0 ? XmlOf(JsonSerializer.SerializeToElement(s0)) : null;
            Console.WriteLine($"second connection, same process: equals first connection's read {again.Value == raw.Value}, equals tool {again.Value == toolXml}");
            Console.WriteLine($"first connection re-read: unchanged {first.Value == raw.Value}");
        }

        // Six back-to-back raw reads on one connection: A = equal to the first of them, B = not.
        var seq = new List<string>();
        string? firstOfSeq = null;
        for (var k = 0; k < 6; k++)
        {
            var r = await Raw.RunAsync(ct => gateway.GetPageContentAsync(pageId, PageInfo.Basic, ct));
            firstOfSeq ??= r.Value;
            seq.Add(r.Value == firstOfSeq ? "A" : "B");
        }

        Console.WriteLine($"six consecutive raw reads: {string.Join(' ', seq)}");

        // The harness's own comparison on this page, as the pages phase runs it.
        var (rec, _, _) = await Run.CompareAsync(
            "pages",
            "get_page_content",
            "get_page_content:basic",
            pageId,
            () => Raw.RunAsync(ct => gateway.GetPageContentAsync(pageId, PageInfo.Basic, ct)),
            () => tools.CallAsync("get_page_content", new() { ["pageId"] = pageId, ["includeBinary"] = false }),
            (r, t) => r.Ok ? t.Ok && string.Equals(t.Xml, r.Value, StringComparison.Ordinal) : !t.Ok && t.Code == r.Code);
        Console.WriteLine($"harness comparison: match {rec.Match}, retried {rec.Retried}, unstableOffset {rec.Counts.GetValueOrDefault("unstableOffset", -1)}, path {rec.Labels.GetValueOrDefault("unstablePath", "-")}, attr {rec.Labels.GetValueOrDefault("unstableAttr", "-")}");
        if (!raw.Ok || raw.Value is null)
        {
            Console.WriteLine($"raw failed: {raw.Code} {raw.HResultText}");
            return Exit.Precondition;
        }

        var structuredXml = result?.StructuredContent is { } sc ? XmlOf(JsonSerializer.SerializeToElement(sc)) : null;
        var textJson = result?.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        var textXml = textJson is null ? null : XmlOf(JsonDocument.Parse(textJson).RootElement);
        var a = raw.Value;

        Console.WriteLine($"raw: {a.Length} chars; {Census(a)}");
        var columns = XmlFacts.Parse(a).Descendants(XmlFacts.One + "Column").ToList();
        Console.WriteLine($"one:Column elements: {columns.Count}; isLocked=true {columns.Count(c => (string?)c.Attribute("isLocked") == "true")}; attribute names: {string.Join(",", columns.SelectMany(c => c.Attributes().Select(x => x.Name.LocalName)).Distinct())}");
        Report("structuredContent.xml", a, structuredXml);
        Report("text block xml", a, textXml);
        if (structuredXml is not null && textXml is not null)
        {
            Console.WriteLine($"structuredContent.xml == text block xml: {string.Equals(structuredXml, textXml, StringComparison.Ordinal)}");
        }

        return Exit.Ok;
    }

    private static void Report(string label, string a, string? b)
    {
        if (b is null)
        {
            Console.WriteLine($"{label}: absent");
            return;
        }

        Console.WriteLine($"{label}: {b.Length} chars; {Census(b)}");
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            Console.WriteLine($"  identical to raw");
            return;
        }

        var first = 0;
        while (first < a.Length && first < b.Length && a[first] == b[first])
        {
            first++;
        }

        var suffix = 0;
        while (suffix < a.Length - first && suffix < b.Length - first && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }

        Console.WriteLine($"  first differing offset: {first}; length delta (tool - raw): {b.Length - a.Length}");
        Console.WriteLine($"  differing span after common prefix/suffix: raw {a.Length - first - suffix} chars, tool {b.Length - first - suffix} chars");
        if (a.Length == b.Length)
        {
            var positions = 0;
            var pairs = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    positions++;
                    var key = $"{Cp(a[i])}->{Cp(b[i])}";
                    pairs[key] = pairs.GetValueOrDefault(key) + 1;
                }
            }

            Console.WriteLine($"  differing positions: {positions}; substitutions (raw->tool, count): {string.Join(", ", pairs.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key} x{p.Value}"))}");
        }

        if (a.Length == b.Length)
        {
            // Where every difference falls; for numeric attribute values (layout numbers, not content) both values.
            var seen = new HashSet<int>();
            var lines = 0;
            for (var i = first; i < a.Length && lines < 20; i++)
            {
                if (a[i] == b[i])
                {
                    continue;
                }

                var start = i;
                while (start > 0 && (char.IsDigit(a[start - 1]) || a[start - 1] is '.' or '-'))
                {
                    start--;
                }

                if (!seen.Add(start))
                {
                    continue;
                }

                var end = start;
                while (end < a.Length && (char.IsDigit(a[end]) || a[end] is '.' or '-'))
                {
                    end++;
                }

                var (p, at) = PathAt(a, i);
                var ra = a[start..end];
                var rb = b[start..Math.Min(end + 4, b.Length)].TakeWhile(c => char.IsDigit(c) || c is '.' or '-').Aggregate(new StringBuilder(), (sb, c) => sb.Append(c)).ToString();
                var numeric = double.TryParse(ra, NumberStyles.Float, CultureInfo.InvariantCulture, out var na)
                    & double.TryParse(rb, NumberStyles.Float, CultureInfo.InvariantCulture, out var nb);
                Console.WriteLine(numeric && at is not null
                    ? $"  @{start} {p} [{at}] raw {na.ToString(CultureInfo.InvariantCulture)} tool {nb.ToString(CultureInfo.InvariantCulture)} ratio {(nb == 0 ? 0 : na / nb).ToString("0.######", CultureInfo.InvariantCulture)}"
                    : $"  @{i} {p}{(at is null ? "" : $" [{at}]")} (not a numeric attribute; codepoints only)");
                lines++;
            }
        }

        Console.WriteLine($"  raw  chars at offset: {Cps(a, first)}");
        Console.WriteLine($"  tool chars at offset: {Cps(b, first)}");
        var (path, attr) = PathAt(a, first);
        Console.WriteLine($"  element path in raw: {path}{(attr is null ? "" : $" (inside attribute {attr})")}");
    }

    /// <summary>Counts of the character classes that can change in transit. Numbers only.</summary>
    private static string Census(string s)
    {
        int lone = 0, pairs = 0, ctrl = 0, nul = 0, cr = 0, lf = 0, crlf = 0, fffd = 0, nonBmpOk = 0, nonAscii = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                pairs++;
                nonBmpOk++;
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                lone++;
            }

            if (c == '\0')
            {
                nul++;
            }
            else if (c < 0x20 && c is not ('\t' or '\n' or '\r'))
            {
                ctrl++;
            }

            if (c == '\r')
            {
                cr++;
                if (i + 1 < s.Length && s[i + 1] == '\n')
                {
                    crlf++;
                }
            }

            if (c == '\n')
            {
                lf++;
            }

            if (c == '�')
            {
                fffd++;
            }

            if (c > 0x7F)
            {
                nonAscii++;
            }
        }

        return $"surrogatePairs {pairs}, loneSurrogates {lone}, C0controls {ctrl}, NUL {nul}, CR {cr}, LF {lf}, CRLF {crlf}, U+FFFD {fffd}, nonAsciiUnits {nonAscii}, utf8Bytes {Encoding.UTF8.GetByteCount(s)}";
    }

    private static string? XmlOf(JsonElement envelope) =>
        envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("xml", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;

    private static string Cp(char c) => "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);

    private static string Cps(string s, int at) =>
        at >= s.Length ? "(end)" : string.Join(' ', s.Skip(at).Take(8).Select(Cp));

    /// <summary>
    /// The element-name path at <paramref name="offset"/> in the raw XML, by a small scanner, and the attribute name
    /// when the offset falls inside an attribute value. Names only.
    /// </summary>
    internal static (string Path, string? Attr) PathAt(string xml, int offset)
    {
        var stack = new List<string>();
        string P(string? extra = null) => string.Join('/', extra is null ? stack : [.. stack, extra]);
        var i = 0;
        while (i < xml.Length)
        {
            if (xml[i] != '<')
            {
                if (i == offset)
                {
                    return (P("#text"), null);
                }

                i++;
                continue;
            }

            int Skip(string close, string what, out (string, string?)? hit)
            {
                var end = xml.IndexOf(close, i, StringComparison.Ordinal);
                end = end < 0 ? xml.Length : end + close.Length;
                hit = offset >= i && offset < end ? (P(what), null) : null;
                return end;
            }

            if (string.CompareOrdinal(xml, i, "<!--", 0, 4) == 0)
            {
                i = Skip("-->", "#comment", out var h);
                if (h is { } r)
                {
                    return r;
                }

                continue;
            }

            if (string.CompareOrdinal(xml, i, "<![CDATA[", 0, 9) == 0)
            {
                i = Skip("]]>", "#cdata", out var h);
                if (h is { } r)
                {
                    return r;
                }

                continue;
            }

            if (string.CompareOrdinal(xml, i, "<?", 0, 2) == 0 || string.CompareOrdinal(xml, i, "<!", 0, 2) == 0)
            {
                i = Skip(">", "#decl", out var h);
                if (h is { } r)
                {
                    return r;
                }

                continue;
            }

            if (i + 1 < xml.Length && xml[i + 1] == '/')
            {
                var end = xml.IndexOf('>', i);
                end = end < 0 ? xml.Length : end + 1;
                if (offset >= i && offset < end)
                {
                    return (P() + " (end tag)", null);
                }

                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                i = end;
                continue;
            }

            // Start tag.
            var tagStart = i;
            var j = i + 1;
            while (j < xml.Length && !char.IsWhiteSpace(xml[j]) && xml[j] is not ('/' or '>'))
            {
                j++;
            }

            var name = xml[(i + 1)..j];
            var selfClosing = false;
            while (j < xml.Length && xml[j] != '>')
            {
                if (xml[j] == '/')
                {
                    selfClosing = true;
                    j++;
                    continue;
                }

                if (char.IsWhiteSpace(xml[j]))
                {
                    j++;
                    continue;
                }

                var attrStart = j;
                while (j < xml.Length && xml[j] is not ('=' or '>') && !char.IsWhiteSpace(xml[j]))
                {
                    j++;
                }

                var attr = xml[attrStart..j];
                while (j < xml.Length && xml[j] is not ('"' or '\'' or '>'))
                {
                    j++;
                }

                if (j < xml.Length && xml[j] is '"' or '\'')
                {
                    var q = xml[j];
                    var valueEnd = xml.IndexOf(q, j + 1);
                    valueEnd = valueEnd < 0 ? xml.Length : valueEnd;
                    if (offset > j && offset <= valueEnd)
                    {
                        return (P(name), attr);
                    }

                    j = valueEnd + 1;
                }
            }

            var tagEnd = Math.Min(j + 1, xml.Length);
            if (offset >= tagStart && offset < tagEnd)
            {
                return (P(name) + " (start tag)", null);
            }

            if (!selfClosing)
            {
                stack.Add(name);
            }

            i = tagEnd;
        }

        return (P() + " (past end)", null);
    }

    /// <summary>Every CallbackID in the notebook, by parent and grandparent element name and raw binary outcome.</summary>
    public static async Task<int> CallbacksAsync(ComGateway gateway, string notebookId)
    {
        var hierarchy = await gateway.GetHierarchyAsync(notebookId, HierarchyScope.Pages, CancellationToken.None);
        var model = XmlFacts.Model(notebookId, hierarchy);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var pages = model.Pages.Where(p => !p.InRecycleBin).ToList();
        var done = 0;
        foreach (var page in pages)
        {
            var basic = await Raw.RunAsync(ct => gateway.GetPageContentAsync(page.Id, PageInfo.Basic, ct));
            Progress.Report("diag-callbacks", ++done, pages.Count);
            if (basic is not { Ok: true, Value: { } xml })
            {
                continue;
            }

            foreach (var cb in XmlFacts.Parse(xml).Descendants(XmlFacts.One + "CallbackID"))
            {
                var id = (string?)cb.Attribute("callbackID") ?? string.Empty;
                if (id.Length == 0)
                {
                    continue;
                }

                var parent = cb.Parent;
                var where = $"{Name(parent?.Parent)}/{Name(parent)}";
                var bin = await Raw.RunAsync(ct => gateway.GetBinaryPageContentAsync(page.Id, id, ct));
                var outcome = bin.Ok ? "returned" : bin.HResult == BinaryObjectDoesNotExist ? "unavailable" : bin.Code + ":" + bin.HResultText;
                var key = $"{where} -> {outcome}";
                counts[key] = counts.GetValueOrDefault(key) + 1;
                if (!bin.Ok && bin.HResult != BinaryObjectDoesNotExist)
                {
                    Console.WriteLine($"page {page.Id}: {where} -> {outcome}");
                }
            }
        }

        foreach (var (k, v) in counts)
        {
            Console.WriteLine($"{v,5}  {k}");
        }

        return Exit.Ok;
    }

    private static string Name(XElement? e) =>
        e is null ? "-" : (e.Name.Namespace == XmlFacts.One ? "one:" : "") + e.Name.LocalName;
}
