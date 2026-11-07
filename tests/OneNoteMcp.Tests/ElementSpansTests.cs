using System.Xml.Linq;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>AD-20's span scanner: exact offsets, refs, labels and counts, never by re-serialising (AD-4).</summary>
public sealed class ElementSpansTests
{
    private const string Ns = BigXml.Ns;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void EverySpan_IsTheElementsExactText(string newline)
    {
        var xml = BigXml.Page(outlines: 3, oes: 4, hugeT: 500, newline: newline);
        var tree = ElementSpans.Page(xml);

        Assert.Equal(0, tree.Root.Start);
        Assert.Equal(xml.Length, tree.Root.End);
        Assert.Equal("one:Page", tree.Root.Element);
        foreach (var node in tree.Nodes.Skip(1))
        {
            var text = xml[node.Start..node.End];
            Assert.StartsWith("<" + node.Element, text, StringComparison.Ordinal);
            Assert.EndsWith(">", text, StringComparison.Ordinal);

            // Wrapped with the root's namespace declarations, the span parses to an element of the same name.
            var parsed = XElement.Parse($"<w xmlns:one=\"{Ns}\">{text}</w>").Elements().Single();
            Assert.Equal(node.Element, "one:" + parsed.Name.LocalName);
            Assert.Equal(node.Children.Count, parsed.Elements().Count());
        }
    }

    [Fact]
    public void SpansAreInDocumentOrder_AndChildrenLieInsideTheirParent()
    {
        var tree = ElementSpans.Page(BigXml.Page(outlines: 3, oes: 4));

        for (var i = 1; i < tree.Nodes.Count; i++)
        {
            Assert.True(tree.Nodes[i - 1].Start < tree.Nodes[i].Start);
            Assert.Equal(i, tree.Nodes[i].Order);
            var node = tree.Nodes[i];
            Assert.InRange(node.Start, node.Parent!.Start, node.Parent.End);
            Assert.InRange(node.End, node.Parent.Start, node.Parent.End);
        }
    }

    [Fact]
    public void Refs_AreObjectIds_ElsePositional_AndAReusedIdBecomesPositional()
    {
        var xml = $"""<one:Page xmlns:one="{Ns}" ID="p"><one:Title><one:OE objectID="a"><one:T>x</one:T></one:OE></one:Title><one:Outline objectID="a"><one:OEChildren><one:OE objectID="b"/></one:OEChildren></one:Outline></one:Page>""";

        var tree = ElementSpans.Page(xml);

        Assert.Equal(["/", "/0", "a", "a/0", "/1", "/1/0", "b"], tree.Nodes.Select(n => n.Ref));
        Assert.Same(tree.Root, tree.Find("/"));
        Assert.Equal("one:Title", tree.Find("/0")!.Element);
        Assert.Equal("one:Outline", tree.Find("/1")!.Element);
        Assert.Null(tree.Find("{nope}"));
        Assert.Equal(["a", "b"], tree.Nodes.Where(n => n.HashId is not null && n.Element == "one:OE").Select(n => n.HashId));
    }

    [Fact]
    public void Hierarchy_RefsAreIds_LabelsAreNames()
    {
        var xml = $"""<?xml version="1.0"?><one:Notebooks xmlns:one="{Ns}"><one:Notebook name="A &amp; B" ID="{"{"}N{"}"}"><one:Section name="S" ID="s1" /><one:Section ID="s2" /></one:Notebook></one:Notebooks>""";

        var tree = ElementSpans.Hierarchy(xml);

        Assert.Equal(["/", "{N}", "s1", "s2"], tree.Nodes.Select(n => n.Ref));
        Assert.Equal(["A & B", "S", null], tree.Nodes.Skip(1).Select(n => n.Label));
        Assert.All(tree.Nodes, n => Assert.Null(n.HashId));
        Assert.Equal(Ns, tree.Namespaces["xmlns:one"]);
    }

    [Fact]
    public void AnAttributeValueHoldingAGreaterThan_DoesNotEndTheTag()
    {
        var xml = $"""<one:Notebooks xmlns:one="{Ns}"><one:Section name="a > b" ID="s" /><one:Section name="c" ID="t"></one:Section></one:Notebooks>""";

        var tree = ElementSpans.Hierarchy(xml);

        Assert.Equal("""<one:Section name="a > b" ID="s" />""", xml[tree.Find("s")!.Start..tree.Find("s")!.End]);
        Assert.Equal("""<one:Section name="c" ID="t"></one:Section>""", xml[tree.Find("t")!.Start..tree.Find("t")!.End]);
    }

    [Fact]
    public void Contains_CountsTablesImagesAndFilesBelow_NotItself()
    {
        var xml = $"""<one:Page xmlns:one="{Ns}"><one:Outline objectID="o"><one:OEChildren><one:OE objectID="e"><one:Table><one:Row><one:Cell><one:OEChildren><one:OE objectID="f"><one:Image /></one:OE></one:OEChildren></one:Cell></one:Row></one:Table></one:OE><one:OE objectID="g"><one:InsertedFile /></one:OE></one:OEChildren></one:Outline></one:Page>""";

        var tree = ElementSpans.Page(xml);

        var outline = tree.Find("o")!;
        Assert.Equal((1, 1, 1), (outline.Tables, outline.Images, outline.InsertedFiles));
        var e = tree.Find("e")!;
        Assert.Equal((1, 1, 0), (e.Tables, e.Images, e.InsertedFiles));
        var table = tree.Find("e/0")!;
        Assert.Equal((0, 1, 0), (table.Tables, table.Images, table.InsertedFiles));
    }

    [Fact]
    public void PageLabels_AreTheFirst80CharactersOfText_TagsStripped_EntitiesDecoded()
    {
        var xml = $"""<one:Page xmlns:one="{Ns}"><one:Outline objectID="o"><one:OEChildren><one:OE objectID="a"><one:T><![CDATA[<span style='x'>One</span> &amp; two]]></one:T></one:OE><one:OE objectID="b"><one:T><![CDATA[{new string('z', 100)}]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>""";

        var tree = ElementSpans.Page(xml);

        Assert.Equal("One & two", tree.Find("a")!.Label);
        Assert.Equal("One & two\n" + new string('z', 70), tree.Find("o")!.Label);
        Assert.Equal(new string('z', 80), tree.Find("b")!.Label);
    }

    [Fact]
    public void Text_IsOneRootSpanningEverything()
    {
        var tree = ElementSpans.Text("abc");

        var root = Assert.Single(tree.Nodes);
        Assert.Equal(("/", 0, 3, (string?)null), (root.Ref, root.Start, root.End, root.Element));
        Assert.Empty(root.Children);
        Assert.Empty(tree.Namespaces);
    }

    [Fact]
    public void Within_ListsOnlyNodesWhollyInsideTheRange()
    {
        var xml = BigXml.Page(outlines: 2, oes: 3);
        var tree = ElementSpans.Page(xml);
        var outline = tree.Find("{O1}{1}{B0}")!;

        var inside = tree.Within(outline.Start, outline.End).Select(n => n.Ref).ToList();

        Assert.Contains("{O1}{1}{B0}", inside);
        Assert.Contains("{O1E2}{1}{B0}", inside);
        Assert.DoesNotContain("{O0}{1}{B0}", inside);
        Assert.DoesNotContain("/", inside);
        Assert.DoesNotContain("{O1}{1}{B0}", tree.Within(outline.Start, outline.End - 1).Select(n => n.Ref));
    }

    [Fact]
    public void MalformedXml_ThrowsWithoutQuotingContent()
    {
        var ex = Assert.Throws<OneNoteXmlException>(() => ElementSpans.Hierarchy($"""<one:Notebooks xmlns:one="{Ns}"><secret>"""));

        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
    }
}
