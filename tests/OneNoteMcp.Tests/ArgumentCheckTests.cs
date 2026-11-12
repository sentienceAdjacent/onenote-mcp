using System.Text.Json;
using OneNoteMcp.Server.Tools;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>Story 2.14: a call's arguments checked against its tool's input schema, and the <c>InvalidArgument</c> message.</summary>
public sealed class ArgumentCheckTests
{
    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "flag": { "type": "boolean", "default": false },
            "count": { "type": ["integer", "null"], "default": null },
            "size": { "type": "integer" },
            "ratio": { "type": "number" },
            "mode": { "type": "string", "enum": ["self", "children"], "default": "self" },
            "run": { "type": ["array", "null"], "items": { "type": ["string", "null"] }, "default": null },
            "any": {}
          },
          "required": ["id"],
          "additionalProperties": false
        }
        """;

    private static readonly JsonElement InputSchema = JsonDocument.Parse(Schema).RootElement;

    private const string Trailer = "Its arguments are: id, flag, count, size, ratio, mode, run, any.";

    public static TheoryData<string, string> ValidArguments() => new()
    {
        { "id", "\"x\"" },
        { "flag", "true" },
        { "flag", "false" },
        { "count", "null" },
        { "count", "2147483647" },
        { "count", "-2147483648" },
        { "size", "0" },
        { "ratio", "1.5" },
        { "mode", "\"children\"" },
        { "run", "null" },
        { "run", "[]" },
        { "run", "[\"a\", null]" },
        { "any", "{\"x\": [1]}" },
    };

    [Theory]
    [MemberData(nameof(ValidArguments))]
    public void ValuesThatFitTheSchema_HaveNoProblems(string name, string json)
    {
        var arguments = Arguments(("id", "\"x\""), (name, json));

        Assert.False(ArgumentCheck.Problems(InputSchema, arguments).Any);
        Assert.Null(ArgumentCheck.Check("t", InputSchema, arguments));
    }

    public static TheoryData<string, string, string> InvalidArguments() => new()
    {
        { "id", "5", "t's argument \"id\" must be a string (it got a number)." },
        { "id", "null", "t's argument \"id\" must be a string (it got null)." },
        { "id", "true", "t's argument \"id\" must be a string (it got a boolean)." },
        { "id", "[\"x\"]", "t's argument \"id\" must be a string (it got an array)." },
        { "id", "{}", "t's argument \"id\" must be a string (it got an object)." },
        { "flag", "\"true\"", "t's argument \"flag\" must be true or false (it got a string)." },
        { "flag", "1", "t's argument \"flag\" must be true or false (it got a number)." },
        { "count", "1.5", "t's argument \"count\" must be an integer or null (it got a number that isn't a 32-bit integer)." },
        { "count", "2147483648", "t's argument \"count\" must be an integer or null (it got a number that isn't a 32-bit integer)." },
        { "count", "1.0", "t's argument \"count\" must be an integer or null (it got a number that isn't a 32-bit integer)." },
        { "count", "\"5\"", "t's argument \"count\" must be an integer or null (it got a string)." },
        { "size", "null", "t's argument \"size\" must be an integer (it got null)." },
        { "ratio", "\"1\"", "t's argument \"ratio\" must be a number (it got a string)." },
        { "mode", "\"bogus\"", "t's argument \"mode\" must be one of: self, children (it got a string that isn't one of them)." },
        { "mode", "\"Self\"", "t's argument \"mode\" must be one of: self, children (it got a string that isn't one of them)." },
        { "mode", "\"self \"", "t's argument \"mode\" must be one of: self, children (it got a string that isn't one of them)." },
        { "mode", "1", "t's argument \"mode\" must be one of: self, children (it got a number)." },
        { "run", "\"a\"", "t's argument \"run\" must be an array of strings or nulls, or null (it got a string)." },
        { "run", "[\"a\", 1]", "t's argument \"run\" must be an array of strings or nulls, or null (it got an array with an item that is a number)." },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void ValuesThatDontFit_AreNamed_WithWhatTheyExpect_AndTheKindTheyGot(string name, string json, string sentence)
    {
        var message = ArgumentCheck.Check("t", InputSchema, Arguments(("id", "\"x\""), (name, json)));

        Assert.Equal(sentence + " " + Trailer, message);
    }

    [Fact]
    public void Messages_NeverEchoTheValue()
    {
        var message = ArgumentCheck.Check(
            "t",
            InputSchema,
            Arguments(("id", "\"secret-1\""), ("mode", "\"secret-2\""), ("flag", "\"secret-3\""), ("run", "[\"ok\", \"secret-4\", 7]")))!;

        Assert.DoesNotContain("secret", message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRequiredArgument_IsNamed_WithWhatItExpects()
    {
        Assert.Equal("t is missing the required argument \"id\" (a string). " + Trailer, ArgumentCheck.Check("t", InputSchema, Arguments()));
    }

    [Fact]
    public void NoArgumentsAtAll_IsTheSameAsAnEmptySet()
    {
        Assert.Equal(ArgumentCheck.Check("t", InputSchema, Arguments()), ArgumentCheck.Check("t", InputSchema, null));
    }

    [Fact]
    public void APresentNullForARequiredArgument_IsATypeProblem_NotAMissingOne()
    {
        var problems = ArgumentCheck.Problems(InputSchema, Arguments(("id", "null")));

        Assert.Empty(problems.Missing);
        Assert.Equal(new[] { new ArgumentProblem("id", "a string", "null") }, problems.Invalid);
    }

    [Fact]
    public void SeveralProblems_AreOneMessage_MissingThenInvalidInSchemaOrderThenUnknown()
    {
        var message = ArgumentCheck.Check(
            "t",
            InputSchema,
            Arguments(("zeta", "1"), ("mode", "\"x\""), ("flag", "0"), ("alpha", "2")));

        Assert.Equal(
            "t is missing the required argument \"id\" (a string). "
            + "t's argument \"flag\" must be true or false (it got a number). "
            + "t's argument \"mode\" must be one of: self, children (it got a string that isn't one of them). "
            + "t doesn't declare the arguments \"alpha\", \"zeta\". "
            + Trailer,
            message);
    }

    [Fact]
    public void OnlyUnknownNames_IsStory25sMessage_Exactly()
    {
        var hierarchy = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"startNodeId\":{\"type\":\"string\"},\"scope\":{\"type\":\"string\",\"enum\":[\"self\"]}},\"additionalProperties\":false}").RootElement;

        Assert.Equal(
            "get_hierarchy doesn't declare the argument \"scop\". Its arguments are: startNodeId, scope.",
            ArgumentCheck.Check("get_hierarchy", hierarchy, Arguments(("scop", "\"sections\""))));
    }

    [Fact]
    public void UnknownNames_AreRefused_EvenWhenTheSchemaAllowsAdditionalProperties()
    {
        var open = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}},\"additionalProperties\":true}").RootElement;

        Assert.Equal(["b"], ArgumentCheck.Problems(open, Arguments(("b", "1"))).Unknown);
    }

    [Fact]
    public void AToolWithoutArguments_SaysSo()
    {
        var none = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement;

        Assert.Null(ArgumentCheck.Check("list_grants", none, null));
        Assert.Equal("list_grants doesn't declare the argument \"x\". It takes no arguments.", ArgumentCheck.Check("list_grants", none, Arguments(("x", "1"))));
    }

    [Theory]
    [InlineData("{\"type\":\"string\"}", "a string")]
    [InlineData("{\"type\":\"boolean\"}", "true or false")]
    [InlineData("{\"type\":[\"boolean\",\"null\"]}", "true or false, or null")]
    [InlineData("{\"type\":[\"integer\",\"null\"]}", "an integer or null")]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}", "an array of strings")]
    [InlineData("{\"type\":\"array\"}", "an array")]
    [InlineData("{\"type\":\"string\",\"enum\":[\"a\",\"b\"]}", "one of: a, b")]
    [InlineData("{}", "any value")]
    public void Expected_DescribesTheSchema_InWords(string schema, string expected)
    {
        Assert.Equal(expected, ArgumentCheck.Expected(JsonDocument.Parse(schema).RootElement));
    }

    [Fact]
    public void Message_WithNoProblems_IsABug()
    {
        Assert.Throws<ArgumentException>(() => ArgumentCheck.Message("t", ArgumentCheck.Problems(InputSchema, Arguments(("id", "\"x\"")))));
    }

    private static Dictionary<string, JsonElement> Arguments(params (string Name, string Json)[] arguments)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, json) in arguments)
        {
            result[name] = JsonDocument.Parse(json).RootElement.Clone();
        }

        return result;
    }
}
