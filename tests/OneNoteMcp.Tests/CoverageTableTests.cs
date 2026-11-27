using System.Reflection;
using System.Text.RegularExpressions;
using OneNoteMcp.Com.Interop;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Initiative Done when 4, story 4.10 (Decision OQ1 a): every member of the hand-declared <see cref="IApplication"/> (slots
/// 7-35) has a row in the spine's "Coverage of <c>IApplication</c>" table naming a released tool or a dated "Not exposed"
/// Decision; every tool a row names is in the contract snapshot (released = in <c>tool-contract.json</c> on main); and every
/// contract tool is named in some row, except the two that never call COM. The table is the one source: a drifting table fails CI.
/// </summary>
public sealed class CoverageTableTests
{
    private const string Spine = "_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md";
    private const string TableHeading = "## Coverage of `IApplication`";
    private const int FirstSlot = 7;

    /// <summary>Contract tools that call no <c>IApplication</c> member, so no row names them (they never call COM).</summary>
    private static readonly string[] ToolsWithoutCom = ["list_grants", "read_result"];

    /// <summary>A backticked lowercase name in a Tool cell: a tool name, unless it is one of <see cref="ArgumentNames"/>.</summary>
    private static readonly Regex ToolName = new(@"`(?<name>[a-z]+(?:_[a-z]+)*)`", RegexOptions.CultureInvariant);
    /// <summary>Arguments the Tool cells mention that aren't tools: `navigate_to`'s and `navigate_to_url`'s `window`, and
    /// `CloseNotebook`'s `force`.</summary>
    private static readonly string[] ArgumentNames = ["window", "force"];

    private static readonly Regex IsoDate = new(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.CultureInvariant);

    [Fact]
    public void SpineTable_CoversEverySlot_WithReleasedTools_AndNamesEveryComTool() =>
        Assert.Empty(Gaps(ReadSpine(), Slots(), ContractTools()));

    [Fact]
    public void ToolsWithoutCom_AreInTheContract() =>
        Assert.All(ToolsWithoutCom, name => Assert.Contains(name, ContractTools()));

    [Fact]
    public void Table_WithARowRemoved_HasGaps()
    {
        // The red case: drop slot 27's row (QuickFiling), so the slot is uncovered and quick_filing is named nowhere.
        var spine = Regex.Replace(ReadSpine(), @"^\| 27 \|[^\n]*\n", string.Empty, RegexOptions.Multiline);
        var gaps = Gaps(spine, Slots(), ContractTools());

        Assert.Contains(gaps, g => g.Contains("slot 27", StringComparison.Ordinal));
        Assert.Contains(gaps, g => g.Contains("quick_filing", StringComparison.Ordinal));
    }

    [Fact]
    public void Table_NamingAToolNotInTheContract_HasAGap()
    {
        var spine = ReadSpine().Replace("`quick_filing` (4, story 4.9)", "`pick_section` (4, story 4.9)", StringComparison.Ordinal);
        Assert.Contains(Gaps(spine, Slots(), ContractTools()), g => g.Contains("pick_section", StringComparison.Ordinal));
    }

    [Fact]
    public void Table_NamingAOneWordToolNotInTheContract_HasAGap()
    {
        var spine = ReadSpine().Replace("| `publish` (3);", "| `export` (3);", StringComparison.Ordinal);
        Assert.Contains(Gaps(spine, Slots(), ContractTools()), g => g.Contains("export is not in the contract", StringComparison.Ordinal));
    }

    [Fact]
    public void Table_WithAnUndatedDecision_HasAGap()
    {
        var spine = Regex.Replace(ReadSpine(), @"^(\| 34 \| `LanguageSettings` \| none \| )[^\n]*\|", "$1Not exposed |", RegexOptions.Multiline);
        Assert.Contains(Gaps(spine, Slots(), ContractTools()), g => g.Contains("slot 34", StringComparison.Ordinal));
    }

    /// <summary>The gaps between the spine's coverage table and the interface and contract, one message each; empty when it holds.</summary>
    internal static List<string> Gaps(string spine, IReadOnlyList<string> slots, IReadOnlySet<string> contractTools)
    {
        var gaps = new List<string>();
        var rows = Rows(spine, gaps);
        var named = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < slots.Count; i++)
        {
            var slot = FirstSlot + i;
            if (!rows.TryGetValue(slot, out var row))
            {
                gaps.Add($"slot {slot} ({slots[i]}) has no row in the spine's coverage table.");
                continue;
            }

            if (!string.Equals(row.Member, slots[i], StringComparison.Ordinal))
            {
                gaps.Add($"slot {slot}: the table names {row.Member}, IApplication declares {slots[i]}.");
            }

            var tools = ToolName.Matches(row.Tools).Select(m => m.Groups["name"].Value)
                .Where(n => !ArgumentNames.Contains(n)).Distinct(StringComparer.Ordinal).ToList();
            foreach (var tool in tools.Where(t => !contractTools.Contains(t)))
            {
                gaps.Add($"slot {slot}: {tool} is not in the contract snapshot.");
            }

            named.UnionWith(tools);
            var decided = row.Decision.StartsWith("Not exposed", StringComparison.Ordinal) && IsoDate.IsMatch(row.Decision);
            if (tools.Count == 0 && !decided)
            {
                gaps.Add($"slot {slot} ({slots[i]}): no tool and no dated \"Not exposed\" Decision.");
            }
        }

        foreach (var slot in rows.Keys.Where(s => s < FirstSlot || s >= FirstSlot + slots.Count))
        {
            gaps.Add($"slot {slot}: the table has a row for a slot IApplication doesn't declare.");
        }

        foreach (var tool in contractTools.Where(t => !named.Contains(t) && !ToolsWithoutCom.Contains(t)).Order(StringComparer.Ordinal))
        {
            gaps.Add($"{tool} is in the contract but no row of the coverage table names it.");
        }

        return gaps;
    }

    private sealed record Row(string Member, string Tools, string Decision);

    private static Dictionary<int, Row> Rows(string spine, List<string> gaps)
    {
        var rows = new Dictionary<int, Row>();
        var lines = spine.ReplaceLineEndings("\n").Split('\n');
        var start = Array.IndexOf(lines, TableHeading);
        if (start < 0)
        {
            gaps.Add($"the spine has no \"{TableHeading}\" section.");
            return rows;
        }

        foreach (var line in lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("## ", StringComparison.Ordinal)))
        {
            var cells = line.Split('|');
            // "| 7 | `GetHierarchy` | tools | decision |" splits into 6 cells, the outer two empty.
            if (cells.Length < 6 || !int.TryParse(cells[1].Trim(), out var slot))
            {
                continue;
            }

            if (!rows.TryAdd(slot, new Row(cells[2].Trim().Trim('`'), cells[3].Trim(), cells[4].Trim())))
            {
                gaps.Add($"slot {slot} has more than one row.");
            }
        }

        return rows;
    }

    private static string ReadSpine() => File.ReadAllText(RepoPaths.Combine(Spine.Split('/')));

    /// <summary>The declared members in slot order (metadata token, as <see cref="InteropLayoutTests"/>), with a property getter's
    /// <c>get_</c> dropped, so <c>get_Windows</c> reads as the table's <c>Windows</c>.</summary>
    private static List<string> Slots() =>
        [.. typeof(IApplication).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(m => m.MetadataToken)
            .Select(m => m.Name.StartsWith("get_", StringComparison.Ordinal) ? m.Name["get_".Length..] : m.Name)];

    private static HashSet<string> ContractTools() =>
        ContractSnapshot.Tools().Select(t => t!["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
}
