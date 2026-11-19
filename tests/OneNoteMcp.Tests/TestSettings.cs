using System.Text.Json.Nodes;

namespace OneNoteMcp.Tests;

/// <summary>Edits a test's temporary <c>settings.json</c> in place (entry 15).</summary>
internal static class TestSettings
{
    /// <summary>Sets <c>exportRoots</c> to <paramref name="roots"/>, keeping everything else (grants included).</summary>
    public static void SetExportRoots(string settingsPath, params string[] roots)
    {
        var root = File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject() : new JsonObject { ["schemaVersion"] = 1 };
        root["exportRoots"] = new JsonArray([.. roots.Select(r => (JsonNode?)JsonValue.Create(r))]);
        File.WriteAllText(settingsPath, root.ToJsonString());
    }
}
