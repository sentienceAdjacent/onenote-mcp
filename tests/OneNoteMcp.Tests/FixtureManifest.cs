using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneNoteMcp.Tests;

/// <summary>
/// <c>Fixtures/manifest.json</c>: which captured file answers which gateway call (story 6). The integration suite's
/// <c>FixtureCapture</c> writes it; <c>FakeGateway</c> serves from it. This file is compiled into both test
/// projects (the integration project links it), so the two can't disagree on the shape.
/// </summary>
internal sealed class FixtureManifest
{
    public const string FileName = "manifest.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int SchemaVersion { get; set; } = 1;

    public List<FixtureEntry> Entries { get; set; } = [];

    /// <summary>What the capture's probe observed; null until a capture has run.</summary>
    public FixtureProbe? Probe { get; set; }

    /// <summary>The cloud page capture's facts; null until <c>capture-cloud</c> has run.</summary>
    public FixtureCloud? Cloud { get; set; }

    /// <summary>The lookup key for a call: the method name (the port method without <c>Async</c>) and its arguments, in order.</summary>
    public static string Key(string method, params string[] args) => method + "(" + string.Join("|", args) + ")";

    public static FixtureManifest Load(string path) =>
        JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"{path} is empty.");
}

internal sealed class FixtureEntry
{
    /// <summary>The port method without <c>Async</c>, such as <c>GetPageContent</c>.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>The call's arguments as strings, in port order (enums by name, booleans as <c>true</c>/<c>false</c>).</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>The result file, relative to <c>Fixtures/</c>. Null when the call failed (<see cref="Error"/>).</summary>
    public string? File { get; set; }

    /// <summary>What the gateway threw instead of returning, as OneNote reported it.</summary>
    public FixtureError? Error { get; set; }

    /// <summary><c>mcp-test</c>, <c>cloud</c> (the one sanitised cloud capture) or <c>hand-built</c>.</summary>
    public string Source { get; set; } = "mcp-test";

    /// <summary>What the capture is of, for people reading the manifest.</summary>
    public string? Note { get; set; }

    [JsonIgnore]
    public string Key => FixtureManifest.Key(Method, [.. Args]);
}

internal sealed class FixtureError
{
    /// <summary>The <c>ErrorCode</c> name.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>The HRESULT as <c>0x8004xxxx</c>, when there was one.</summary>
    public string? HResult { get; set; }

    public string Message { get; set; } = string.Empty;
}

internal sealed class FixtureProbe
{
    public string CapturedOn { get; set; } = string.Empty;

    /// <summary>The probe page read before the edit (piBasic), relative to <c>Fixtures/</c>; its manifest entry is the read after.</summary>
    public string BeforeFile { get; set; } = string.Empty;

    public string PageId { get; set; } = string.Empty;

    /// <summary>The OE whose text the probe changed.</summary>
    public string EditedObjectId { get; set; } = string.Empty;

    /// <summary>The Outline the probe didn't touch.</summary>
    public string UntouchedOutlineId { get; set; } = string.Empty;

    /// <summary>Attributes OneNote changed anywhere on the page outside the edited OE (root Page and ancestors included), as <c>element@attribute</c>.</summary>
    public List<string> ChangedAttributesOnUntouched { get; set; } = [];

    /// <summary>Of those, the ones <c>ElementHash</c> does not exclude: reported to Phill, never added silently.</summary>
    public List<string> UnexcludedChanges { get; set; } = [];

    /// <summary>Whether the image's <c>callbackID</c> was the same in two piBasic reads and after the edit.</summary>
    public bool? CallbackIdStable { get; set; }

    /// <summary>Whether OneNote accepted each seed's partial <c>UpdatePageContent</c>, by seed page title.</summary>
    public Dictionary<string, string> SeedResults { get; set; } = [];

    public List<string> Notes { get; set; } = [];
}

internal sealed class FixtureCloud
{
    public string CapturedOn { get; set; } = string.Empty;

    /// <summary><c>cloud</c> when captured from the cloud page, <c>hand-built</c> when the fallback was used.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The page's placeholder ID in the fixtures.</summary>
    public string PageId { get; set; } = string.Empty;

    public int BasicImageCount { get; set; }

    public int BinaryImageCount { get; set; }

    public List<string> Notes { get; set; } = [];
}
