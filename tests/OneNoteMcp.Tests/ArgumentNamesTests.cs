using System.Text.Json;
using OneNoteMcp.Server.Tools;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>Story 2.5: which argument names a tool doesn't declare, and the <c>InvalidArgument</c> message that names them.</summary>
public sealed class ArgumentNamesTests
{
    private static readonly JsonElement HierarchySchema = JsonDocument.Parse(
        "{\"type\":\"object\",\"properties\":{\"startNodeId\":{\"type\":\"string\"},\"scope\":{\"type\":\"string\"}}}").RootElement;

    [Fact]
    public void Declared_IsThePropertyNames_InSchemaOrder()
    {
        Assert.Equal(["startNodeId", "scope"], ArgumentNames.Declared(HierarchySchema));
    }

    [Theory]
    [InlineData("{\"type\":\"object\"}")]
    [InlineData("{\"type\":\"object\",\"properties\":{}}")]
    [InlineData("{\"type\":\"object\",\"properties\":null}")]
    [InlineData("true")]
    public void Declared_IsEmpty_WithoutProperties(string schema)
    {
        Assert.Empty(ArgumentNames.Declared(JsonDocument.Parse(schema).RootElement));
    }

    [Fact]
    public void Unknown_IsEmpty_ForDeclaredNamesOrNoArguments()
    {
        string[] declared = ["startNodeId", "scope"];

        Assert.Empty(ArgumentNames.Unknown(declared, ["scope", "startNodeId"]));
        Assert.Empty(ArgumentNames.Unknown(declared, []));
        Assert.Empty(ArgumentNames.Unknown(declared, null));
    }

    [Fact]
    public void Unknown_IsCaseSensitive_AndSortedOrdinally()
    {
        // The SDK binds names ordinally, so Scope and SCOPE are as unknown as scop.
        var unknown = ArgumentNames.Unknown(["startNodeId", "scope"], ["zeta", "scope", "Scope", "alpha", "SCOPE", "scop"]);

        Assert.Equal(["SCOPE", "Scope", "alpha", "scop", "zeta"], unknown);
    }

    [Fact]
    public void Unknown_WithNothingDeclared_IsEveryName()
    {
        Assert.Equal(["x", "y"], ArgumentNames.Unknown([], ["y", "x"]));
    }

    [Fact]
    public void Message_NamesTheArgument_AndListsTheDeclaredOnes()
    {
        Assert.Equal(
            "get_hierarchy doesn't declare the argument \"scop\". Its arguments are: startNodeId, scope.",
            ArgumentNames.Message("get_hierarchy", ["startNodeId", "scope"], ["scop"]));
    }

    [Fact]
    public void Message_NamesSeveralArguments_InTheOrderGiven()
    {
        Assert.Equal(
            "find_pages doesn't declare the arguments \"alpha\", \"zeta\". Its arguments are: query, startNodeId, fromRoot.",
            ArgumentNames.Message("find_pages", ["query", "startNodeId", "fromRoot"], ["alpha", "zeta"]));
    }

    [Fact]
    public void Message_ForAToolWithoutArguments_SaysSo()
    {
        Assert.Equal(
            "list_grants doesn't declare the argument \"x\". It takes no arguments.",
            ArgumentNames.Message("list_grants", [], ["x"]));
    }

    [Fact]
    public void Message_NamesTen_CutsLongNames_AndCountsTheRest()
    {
        var longName = new string('a', 200);
        var names = ArgumentNames.Unknown([], [longName, .. Enumerable.Range(0, 11).Select(i => $"n{i:D2}")]);

        var message = ArgumentNames.Message("t", [], names);

        Assert.Equal(12, names.Count);
        var cut = "\"" + new string('a', ArgumentNames.MaxNameLength) + "…\"";
        Assert.Equal(
            $"t doesn't declare the arguments {cut}, \"n00\", \"n01\", \"n02\", \"n03\", \"n04\", \"n05\", \"n06\", \"n07\", \"n08\" and 2 more. It takes no arguments.",
            message);
        Assert.DoesNotContain(new string('a', ArgumentNames.MaxNameLength + 1), message, StringComparison.Ordinal);
    }

    [Fact]
    public void Message_JsonQuotesNames_SoQuotesAndControlCharactersStayInside()
    {
        var message = ArgumentNames.Message("t", ["a"], ["x\"y", "line\nbreak"]);

        Assert.Equal("t doesn't declare the arguments \"x\\\"y\", \"line\\nbreak\". Its arguments are: a.", message);
    }

    [Fact]
    public void Message_DoesNotSplitASurrogatePair_AtTheCut()
    {
        // 63 ASCII characters, then an emoji (two UTF-16 units) that straddles the 64-character cut.
        var name = new string('b', ArgumentNames.MaxNameLength - 1) + "\U0001F600" + "tail";

        var message = ArgumentNames.Message("t", [], [name]);

        Assert.Contains("\"" + new string('b', ArgumentNames.MaxNameLength - 1) + "…\"", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Message_WithNoUnknownNames_IsABug()
    {
        Assert.Throws<ArgumentException>(() => ArgumentNames.Message("t", ["a"], []));
    }
}
