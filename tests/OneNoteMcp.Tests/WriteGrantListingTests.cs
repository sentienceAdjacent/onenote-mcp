using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 2.6: <c>list_grants</c> shows AD-6's write grants, one <c>writeNotebook</c> row per <c>notebooks{}</c> entry
/// with <c>write: true</c>, beside the read grants (<see cref="ReadGrantSettingsTests"/>), over <see cref="FakeGateway"/>
/// and a temporary <c>settings.json</c>. Made-up notebooks only.
/// </summary>
public sealed class WriteGrantListingTests : IDisposable
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();

    public WriteGrantListingTests()
    {
        // Open: notebook A (local, trailing separator) with section S, and notebook B on OneDrive.
        gateway.Hierarchies[(string.Empty, HierarchyScope.Sections)] =
            "<?xml version=\"1.0\"?>\n<one:Notebooks " + Ns + ">"
            + "<one:Notebook name=\"A\" nickname=\"Notebook A\" ID=\"{NA}{1}{B0}\" path=\"C:\\notebooks\\A\\\">"
            + "<one:Section name=\"S\" ID=\"{S}{1}{B0}\" path=\"C:\\notebooks\\A\\S.one\" /></one:Notebook>"
            + "<one:Notebook name=\"folder-b\" nickname=\"Notebook B\" ID=\"{NB}{1}{B0}\" path=\"https://d.docs.live.net/abc/Documents/folder-b/\" />"
            + "</one:Notebooks>";
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SettingsStore Store => new(temp.Combine("settings.json"), temp.Combine("journal"));

    public void Dispose() => temp.Dispose();

    private async Task<ToolSuccess> ListAsync() =>
        await new ListGrantsUseCase(gateway, Store.Read, clock).ExecuteAsync(new ToolCallContext("c"), Ct);

    private void Save(string key, string name, bool write = true, string via = GrantedVia.Cli) =>
        new SettingsWriter(Store, clock).SaveWriteGrant(new NotebookGrant(key, name, "{OLD}{1}{B0}", write, PermanentDelete: false, clock.GetUtcNow(), via));

    [Fact]
    public async Task AWriteGrant_IsAWriteNotebookRow_ActiveWhileItsNotebookIsOpen_UntilRevoked()
    {
        Save(@"C:\notebooks\A", "Notebook A", via: GrantedVia.Elicitation);
        Save("https://d.docs.live.net/abc/Documents/folder-b", "Notebook B", via: GrantedVia.Created);
        Save(@"C:\notebooks\Gone", "Gone");

        var success = await ListAsync();

        var at = SettingsWriter.Format(clock.GetUtcNow());
        Assert.Equal(
            [
                new GrantListing("writeNotebook", @"C:\notebooks\A", "Notebook A", @"C:\notebooks\A", "{OLD}{1}{B0}", at, GrantedVia.Elicitation, "untilRevoked", "active"),
                new GrantListing("writeNotebook", @"C:\notebooks\Gone", "Gone", @"C:\notebooks\Gone", "{OLD}{1}{B0}", at, GrantedVia.Cli, "untilRevoked", "notebookNotOpen"),
                new GrantListing("writeNotebook", "https://d.docs.live.net/abc/Documents/folder-b", "Notebook B", "https://d.docs.live.net/abc/Documents/folder-b", "{OLD}{1}{B0}", at, GrantedVia.Created, "untilRevoked", "active"),
            ],
            success.Meta.Grants);
        Assert.Empty(success.Warnings);
        Assert.Equal([(string.Empty, HierarchyScope.Sections)], gateway.Calls);
    }

    [Fact]
    public async Task AnEntryWithWriteFalse_IsNotListed()
    {
        Save(@"C:\notebooks\A", "Notebook A", write: false);

        var success = await ListAsync();

        Assert.Empty(success.Meta.Grants!);
        Assert.Empty(gateway.Calls); // no grants to list: no COM call
    }

    [Fact]
    public async Task ReadAndWriteGrants_ShareOneHierarchyRead_ReadRowsFirst()
    {
        Save(@"C:\notebooks\A", "Notebook A");
        new SettingsWriter(Store, clock).SaveReadGrant(new ProtectedSectionGrant(@"C:\notebooks\A\S.one", "S", @"C:\notebooks\A", "{S}{1}{B0}", clock.GetUtcNow(), GrantedVia.Cli));

        var grants = (await ListAsync()).Meta.Grants!;

        Assert.Equal([("readSection", "active"), ("writeNotebook", "active")], grants.Select(g => (g.Kind, g.Status)));
        Assert.Single(gateway.Calls);
    }

    [Fact]
    public async Task NoGrants_MakesNoComCall()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");

        Assert.Empty((await ListAsync()).Meta.Grants!);
        Assert.Empty(gateway.Calls);
    }

    [Theory]
    [InlineData(@"C:\\notebooks\\A", @"C:\\notebooks\\A")] // exact
    [InlineData(@"C:\\notebooks\\A", @"c:\\NOTEBOOKS\\a")] // case only
    public async Task DuplicateNotebookKeys_ListsEach_SaysTheyCountAsAbsent_AndNamesTheWriteRevoke(string first, string second)
    {
        var entry = """{"name": "Notebook A", "idAtGrant": "i", "write": true, "permanentDelete": false, "grantedAt": "2026-11-07T12:00:00Z", "grantedVia": "cli"}""";
        File.WriteAllText(Store.Path, "{\"schemaVersion\": 1, \"notebooks\": {\"" + first + "\": " + entry + ", \"" + second + "\": " + entry + "}}");

        var success = await ListAsync();

        Assert.Equal(2, success.Meta.Grants!.Count);
        var warning = Assert.Single(success.Warnings);
        Assert.Equal(ToolWarnings.SettingsInvalid, warning.Code);
        Assert.Contains("none of the listed grants with lifetime hours or untilRevoked applies", warning.Message, StringComparison.Ordinal);
        Assert.Contains("grant --revoke --write \"<notebook name>\"", warning.UserAction!, StringComparison.Ordinal);
        Assert.DoesNotContain("--read-section", warning.UserAction!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APermanentDeleteGrant_IsItsOwnRow_AfterTheWriteRows_OnlyBesideAWriteGrant()
    {
        // Story 5.6 plan Q2: kind permanentDeleteNotebook. One held without a write grant (an older file) is inert and unlisted.
        Save(@"C:\notebooks\A", "Notebook A");
        new SettingsWriter(Store, clock).SavePermanentDeleteGrant(@"C:\notebooks\A");
        Save(@"C:\notebooks\Gone", "Gone");
        var json = File.ReadAllText(Store.Path).Replace(
            "\"notebooks\": {",
            "\"notebooks\": {\"C:\\\\notebooks\\\\Inert\": {\"name\": \"Inert\", \"idAtGrant\": \"i\", \"write\": false, \"permanentDelete\": true, \"grantedAt\": \"2026-11-07T12:00:00.000Z\", \"grantedVia\": \"cli\"},",
            StringComparison.Ordinal);
        File.WriteAllText(Store.Path, json);

        var grants = (await ListAsync()).Meta.Grants!;

        Assert.Equal(["writeNotebook", "writeNotebook", "permanentDeleteNotebook"], grants.Select(g => g.Kind));
        var row = grants[^1];
        Assert.Equal((@"C:\notebooks\A", "Notebook A", @"C:\notebooks\A", "untilRevoked", "active"), (row.Key, row.Name, row.NotebookKey, row.Lifetime, row.Status));
    }

    [Fact]
    public async Task APermanentDeleteRow_IsAlwaysGrantedViaCli_WhateverRelabelledTheWriteGrant()
    {
        // Q1 c: only the CLI stores it; a later write grant from the dialog keeps it but relabels the entry.
        Save(@"C:\notebooks\A", "Notebook A");
        new SettingsWriter(Store, clock).SavePermanentDeleteGrant(@"C:\notebooks\A");
        Save(@"C:\notebooks\A", "Notebook A", via: GrantedVia.Elicitation);

        var grants = (await ListAsync()).Meta.Grants!;

        Assert.Equal(GrantedVia.Elicitation, grants.Single(g => g.Kind == "writeNotebook").GrantedVia);
        Assert.Equal(GrantedVia.Cli, grants.Single(g => g.Kind == "permanentDeleteNotebook").GrantedVia);
    }

    [Fact]
    public async Task DuplicatesInBothAreas_TheAdviceNamesBothRevokes()
    {
        var w = """{"name": "Notebook A", "idAtGrant": "i", "write": true, "permanentDelete": false, "grantedAt": "2026-11-07T12:00:00Z", "grantedVia": "cli"}""";
        var r = """{"name": "S", "notebookKey": "", "idAtGrant": "i", "grantedAt": "2026-11-07T12:00:00Z", "grantedVia": "cli"}""";
        File.WriteAllText(
            Store.Path,
            """{"schemaVersion": 1, "notebooks": {"C:\\notebooks\\A": @W@, "C:\\notebooks\\A": @W@}, "protectedSections": {"C:\\notebooks\\A\\S.one": @R@, "c:\\notebooks\\a\\s.one": @R@}}"""
                .Replace("@W@", w, StringComparison.Ordinal)
                .Replace("@R@", r, StringComparison.Ordinal));

        var warning = Assert.Single((await ListAsync()).Warnings);

        Assert.Contains("--revoke --read-section", warning.UserAction!, StringComparison.Ordinal);
        Assert.Contains("--revoke --write", warning.UserAction!, StringComparison.Ordinal);
    }
}
