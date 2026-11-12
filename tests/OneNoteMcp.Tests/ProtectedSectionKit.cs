using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// A scripted notebook for story 1.20 (AD-21), hand-built and named only "Notebook A": an open section, a protected
/// section <c>Vault</c>, a second protected section also named <c>Vault</c> inside a section group, and a recycle bin.
/// Every <c>GetHierarchy</c> scope is rendered from one model, as OneNote would; page reads return text that contains
/// <see cref="Secret"/> for protected pages, so a test can prove it never leaves the server. Every call is recorded.
/// </summary>
internal sealed class VaultGateway : IOneNoteGateway
{
    public const string NotebookId = "{NB}{1}{B0}";
    public const string NotebookPath = @"C:\notebooks\A";
    public const string OpenSectionId = "{SO}{1}{B0}";
    public const string OpenPageId = "{SO}{1}{E1}";
    public const string VaultId = "{SV}{1}{B0}";
    public const string VaultPath = @"C:\notebooks\A\Vault.one";
    public const string VaultPageId = "{SV}{1}{E1}";
    public const string VaultPage2Id = "{SV}{1}{E2}";
    public const string GroupId = "{G}{1}{B0}";
    public const string GroupVaultId = "{SV2}{1}{B0}";
    public const string GroupVaultPath = @"C:\notebooks\A\Group\Vault.one";
    public const string GroupVaultPageId = "{SV2}{1}{E1}";
    public const string RecycleBinId = "{RB}{1}{B0}";
    public const string DeletedSectionId = "{SD}{1}{B0}";
    public const string DeletedPageId = "{SD}{1}{E1}";
    public const string VaultName = "Vault";
    public const string Secret = "SECRET-VAULT-TEXT";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly XElement notebook;

    public VaultGateway(bool locked = false)
    {
        Locked = locked;
        notebook = new XElement(One + "Notebook",
            new XAttribute("name", "Notebook A"), new XAttribute("nickname", "Notebook A"), new XAttribute("ID", NotebookId), new XAttribute("path", NotebookPath),
            Section("Open", OpenSectionId, @"C:\notebooks\A\Open.one", encrypted: false, Page(OpenPageId, "Open page")),
            Section(VaultName, VaultId, VaultPath, encrypted: true, Page(VaultPageId, "Vault page one"), Page(VaultPage2Id, "Vault page two")),
            new XElement(One + "SectionGroup", new XAttribute("name", "Group"), new XAttribute("ID", GroupId), new XAttribute("path", @"C:\notebooks\A\Group"),
                Section(VaultName, GroupVaultId, GroupVaultPath, encrypted: true, Page(GroupVaultPageId, "Group vault page"))),
            new XElement(One + "SectionGroup", new XAttribute("name", "OneNote_RecycleBin"), new XAttribute("ID", RecycleBinId), new XAttribute("path", @"C:\notebooks\A\OneNote_RecycleBin"), new XAttribute("isRecycleBin", "true"),
                new XElement(One + "Section", new XAttribute("name", "Deleted Pages"), new XAttribute("ID", DeletedSectionId), new XAttribute("path", @"C:\notebooks\A\OneNote_RecycleBin\OneNote_DeletedPages.one"), new XAttribute("isInRecycleBin", "true"), new XAttribute("isDeletedPages", "true"),
                    Page(DeletedPageId, "Deleted page", inRecycleBin: true))));
    }

    /// <summary>True: the protected sections are locked, so OneNote shows no pages in them and page reads give SectionLocked.</summary>
    public bool Locked { get; }

    /// <summary>Every gateway call, as <c>Method:id</c>.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>When set, every FindPages and FindMeta returns it, whatever the start.</summary>
    public string? SearchXml { get; set; }

    /// <summary>Page XML overrides by page ID (a large page, for read_result).</summary>
    public Dictionary<string, string> PageXml { get; } = new(StringComparer.Ordinal);

    public IEnumerable<string> ContentCalls => Calls.Where(c => c.StartsWith("GetPageContent:", StringComparison.Ordinal) || c.StartsWith("GetBinaryPageContent:", StringComparison.Ordinal));

    /// <summary>Renames or moves the Vault: its <c>path</c> changes, as OneNote reports after the user renames it.</summary>
    public void MoveVault(string newPath) => Find(VaultId)!.SetAttributeValue("path", newPath);

    public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct)
    {
        Calls.Add($"GetHierarchy:{startNodeId}:{scope}");
        if (startNodeId.Length == 0)
        {
            var root = new XElement(One + "Notebooks", Render(notebook, scope == HierarchyScope.Self ? HierarchyScope.Notebooks : scope, isStart: false));
            return Task.FromResult(Document(root));
        }

        var node = Find(startNodeId);
        return node is null ? Task.FromException<string>(NotFound("GetHierarchy")) : Task.FromResult(Document(Render(node, scope, isStart: true)));
    }

    public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct)
    {
        Calls.Add($"GetHierarchyParent:{objectId}");
        var node = Find(objectId);
        if (node is null)
        {
            return Task.FromException<string>(NotFound("GetHierarchyParent"));
        }

        return Task.FromResult(node.Parent is { } parent ? (string)parent.Attribute("ID")! : string.Empty);
    }

    public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct)
    {
        Calls.Add($"GetPageContent:{pageId}");
        var node = Find(pageId);
        if (node is null || node.Name != One + "Page")
        {
            return Task.FromException<string>(NotFound("GetPageContent"));
        }

        var section = node.Parent!;
        var isProtected = (string?)section.Attribute("encrypted") == "true";
        if (isProtected && Locked)
        {
            return Task.FromException<string>(new OneNoteException(ErrorCode.SectionLocked, "GetPageContent failed: hrSectionLocked (0x8004201B).", unchecked((int)0x8004201B)));
        }

        if (PageXml.TryGetValue(pageId, out var xml))
        {
            return Task.FromResult(xml);
        }

        var body = isProtected ? $"{Secret} of {pageId}" : $"Open text of {pageId}";
        return Task.FromResult(
            $"<?xml version=\"1.0\"?>\n<one:Page xmlns:one=\"{OneNoteXml.Namespace2013}\" ID=\"{pageId}\" name=\"{(string?)node.Attribute("name")}\" lastModifiedTime=\"2026-11-07T00:00:00.000Z\">"
            + $"<one:Title><one:OE objectID=\"{{T}}{{1}}{{B0}}\"><one:T><![CDATA[{(string?)node.Attribute("name")}]]></one:T></one:OE></one:Title>"
            + $"<one:Outline objectID=\"{{O}}{{1}}{{B0}}\"><one:OEChildren><one:OE objectID=\"{{E}}{{1}}{{B0}}\"><one:T><![CDATA[{body}]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>");
    }

    public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct)
    {
        Calls.Add($"GetBinaryPageContent:{pageId}");
        return Task.FromResult("U0VDUkVU");
    }

    public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct)
    {
        Calls.Add($"FindPages:{startNodeId}");
        return SearchAsync(startNodeId);
    }

    public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct)
    {
        Calls.Add($"FindMeta:{startNodeId}");
        return SearchAsync(startNodeId);
    }

    public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct)
    {
        Calls.Add($"GetHyperlinkToObject:{hierarchyId}");
        return Task.FromResult($"onenote:#{hierarchyId}");
    }

    public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct)
    {
        Calls.Add($"GetWebHyperlinkToObject:{hierarchyId}");
        return Task.FromResult($"https://example.invalid/{hierarchyId}");
    }

    public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => Task.FromResult(@"C:\backup");

    public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> CreateNewPageAsync(string sectionId, NewPageStyle style, CancellationToken ct) => throw new NotSupportedException();

    public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct) => throw new NotSupportedException();

    public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) => throw new NotSupportedException();

    public Task WaitForAbandonedWritesAsync(CancellationToken ct) => Task.CompletedTask;

    private static XElement Section(string name, string id, string path, bool encrypted, params XElement[] pages)
    {
        var section = new XElement(One + "Section", new XAttribute("name", name), new XAttribute("ID", id), new XAttribute("path", path));
        if (encrypted)
        {
            section.SetAttributeValue("encrypted", "true");
        }

        section.Add(pages);
        return section;
    }

    private static XElement Page(string id, string name, bool inRecycleBin = false)
    {
        var page = new XElement(One + "Page", new XAttribute("ID", id), new XAttribute("name", name), new XAttribute("pageLevel", "1"), new XAttribute("lastModifiedTime", "2026-11-07T00:00:00.000Z"),
            new XElement(One + "Meta", new XAttribute("name", "tag"), new XAttribute("content", "x")));
        if (inRecycleBin)
        {
            page.SetAttributeValue("isInRecycleBin", "true");
        }

        return page;
    }

    private static OneNoteException NotFound(string method) =>
        new(ErrorCode.ObjectNotFound, $"{method} failed: hrObjectDoesNotExist (0x80042014).", unchecked((int)0x80042014));

    private static string Document(XElement root)
    {
        // Declare the prefix on the root, as OneNote does.
        root.SetAttributeValue(XNamespace.Xmlns + "one", OneNoteXml.Namespace2013);
        return "<?xml version=\"1.0\"?>\n" + root.ToString(SaveOptions.DisableFormatting);
    }

    private Task<string> SearchAsync(string startNodeId)
    {
        if (SearchXml is not null)
        {
            return Task.FromResult(SearchXml);
        }

        // includeUnindexed: OneNote returns every page under the start (story 9).
        var root = startNodeId.Length == 0 ? new XElement(One + "Notebooks", Render(notebook, HierarchyScope.Pages, isStart: false)) : Find(startNodeId) is { } node ? Render(node, HierarchyScope.Pages, isStart: true) : null;
        return root is null ? Task.FromException<string>(NotFound("FindPages")) : Task.FromResult(Document(root));
    }

    private XElement? Find(string id) =>
        notebook.DescendantsAndSelf().FirstOrDefault(e => (string?)e.Attribute("ID") == id);

    /// <summary>A copy of <paramref name="node"/> as GetHierarchy shows it at <paramref name="scope"/>.</summary>
    private XElement Render(XElement node, HierarchyScope scope, bool isStart)
    {
        var copy = new XElement(node);
        if (Locked)
        {
            foreach (var section in copy.DescendantsAndSelf(One + "Section").Where(s => (string?)s.Attribute("encrypted") == "true"))
            {
                section.SetAttributeValue("locked", "true");
                section.Elements(One + "Page").Remove();
            }
        }

        // Page Meta appears only in search results, never in GetHierarchy (close enough for these tests).
        switch (scope)
        {
            case HierarchyScope.Self:
                copy.Elements().Where(e => e.Name != One + "Meta").Remove();
                break;
            case HierarchyScope.Children:
                foreach (var child in copy.Elements().ToList())
                {
                    child.Elements().Where(e => e.Name != One + "Meta").Remove();
                }

                break;
            case HierarchyScope.Notebooks:
                copy.Elements().Where(e => e.Name != One + "Meta").Remove();
                break;
            case HierarchyScope.Sections:
                copy.Descendants(One + "Page").Remove();
                if (copy.Name == One + "Page")
                {
                    copy.Elements().Remove();
                }

                break;
            case HierarchyScope.Pages:
                break;
        }

        _ = isStart;
        return copy;
    }
}

/// <summary>The grant dialog, scripted: each call takes the next answer, and every question asked is recorded.</summary>
internal sealed class FakePrompt(params GrantPromptResult[] answers) : IGrantPrompt
{
    private readonly Queue<GrantPromptResult> queue = new(answers);

    public List<GrantQuestion> Questions { get; } = [];

    public Task<GrantPromptResult> AskAsync(GrantQuestion question, CancellationToken ct)
    {
        Questions.Add(question);
        return Task.FromResult(queue.Count > 0 ? queue.Dequeue() : new GrantPromptResult(GrantPromptStatus.Cancelled));
    }

    public static GrantPromptResult Accept(string typed, ReadGrantLifetime lifetime, int? hours = null) =>
        new(GrantPromptStatus.Accepted, typed, lifetime, hours);
}

/// <summary>
/// The real runner, result store, access check and use cases over a <see cref="VaultGateway"/>, with a temporary
/// <c>settings.json</c> and a clock the test moves.
/// </summary>
internal sealed class VaultHarness : IDisposable
{
    private readonly TempDirectory temp = new();

    public VaultHarness(bool locked = false, int inlineMaxChars = 100_000)
    {
        Gateway = new VaultGateway(locked);
        SettingsPath = temp.Combine("settings.json");
        Settings = new SettingsStore(SettingsPath);
        Writer = new SettingsWriter(Settings, Clock);
        Access = new ReadAccess(Gateway, Settings.Read, Writer, Clock);
        ResultSettings = new ResultSettings(inlineMaxChars, TtlMinutes: 30, MaxStoreMB: 16);
        Store = new ResultStore(Clock, () => ResultSettings);
        Runner = new ToolRunner(NullLogger<ToolRunner>.Instance, new ResultDelivery(Store, EnvelopeMeasure.Instance));
        ReadResult = new ReadResultUseCase(Store, EnvelopeMeasure.Instance, Access.Results);
    }

    public VaultGateway Gateway { get; }

    public FakeClock Clock { get; } = new();

    public string SettingsPath { get; }

    public SettingsStore Settings { get; }

    public SettingsWriter Writer { get; }

    public ReadAccess Access { get; }

    public ResultSettings ResultSettings { get; }

    public ResultStore Store { get; }

    public ToolRunner Runner { get; }

    public ReadResultUseCase ReadResult { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Task<CallToolResult> PageContent(string pageId, IGrantPrompt? prompt = null, bool includeBinary = false)
    {
        var useCase = new GetPageContentUseCase(Gateway, Access);
        return Runner.RunAsync("get_page_content", useCase, (c, ct) => useCase.ExecuteAsync(c, pageId, includeBinary, ct), Ct, prompt);
    }

    public Task<CallToolResult> Binary(string pageId, IGrantPrompt? prompt = null)
    {
        var useCase = new GetBinaryPageContentUseCase(Gateway, Access);
        return Runner.RunAsync("get_binary_page_content", useCase, (c, ct) => useCase.ExecuteAsync(c, pageId, "{CB}", ct), Ct, prompt);
    }

    public Task<CallToolResult> Hierarchy(string start, HierarchyScope scope, IGrantPrompt? prompt = null)
    {
        var useCase = new GetHierarchyUseCase(Gateway, Access);
        return Runner.RunAsync("get_hierarchy", useCase, (c, ct) => useCase.ExecuteAsync(c, start, scope, ct), Ct, prompt);
    }

    public Task<CallToolResult> Parent(string id, IGrantPrompt? prompt = null)
    {
        var useCase = new GetHierarchyParentUseCase(Gateway, Access);
        return Runner.RunAsync("get_hierarchy_parent", useCase, (c, ct) => useCase.ExecuteAsync(c, id, ct), Ct, prompt);
    }

    public Task<CallToolResult> Link(string id, IGrantPrompt? prompt = null)
    {
        var useCase = new GetHyperlinkToObjectUseCase(Gateway, Access);
        return Runner.RunAsync("get_hyperlink_to_object", useCase, (c, ct) => useCase.ExecuteAsync(c, id, string.Empty, ct), Ct, prompt);
    }

    public Task<CallToolResult> WebLink(string id, IGrantPrompt? prompt = null)
    {
        var useCase = new GetWebHyperlinkToObjectUseCase(Gateway, Access);
        return Runner.RunAsync("get_web_hyperlink_to_object", useCase, (c, ct) => useCase.ExecuteAsync(c, id, string.Empty, ct), Ct, prompt);
    }

    public Task<CallToolResult> FindPages(string query, string start, PageTextCache? cache = null)
    {
        var useCase = new FindPagesUseCase(Gateway, cache ?? PageTextCache.WithDefaults(), Access);
        return Runner.RunAsync("find_pages", useCase, (c, ct) => useCase.ExecuteAsync(c, query, start, start.Length == 0, ct), Ct);
    }

    public Task<CallToolResult> FindMeta(string start)
    {
        var useCase = new FindMetaUseCase(Gateway, Access);
        return Runner.RunAsync("find_meta", useCase, (c, ct) => useCase.ExecuteAsync(c, "tag", start, start.Length == 0, ct), Ct);
    }

    public Task<CallToolResult> Read(string resultId, string @ref = "", string expand = "", int? offset = null, string[]? run = null)
    {
        return Runner.RunAsync("read_result", ReadResult, (c, ct) => ReadResult.ExecuteAsync(c, resultId, @ref, run, expand, null, offset, null, ct), Ct);
    }

    public Task<CallToolResult> ListGrants()
    {
        var useCase = new ListGrantsUseCase(Gateway, Settings.Read, Clock, Access);
        return Runner.RunAsync("list_grants", useCase, (c, ct) => useCase.ExecuteAsync(c, ct), Ct);
    }

    /// <summary>Stores a read grant for <paramref name="path"/> as the CLI would.</summary>
    public void Grant(string path, string sectionId, DateTimeOffset? expiresAt = null) =>
        Writer.SaveReadGrant(new ProtectedSectionGrant(GrantKeys.SectionKey(path), VaultGateway.VaultName, VaultGateway.NotebookPath, sectionId, Clock.GetUtcNow(), GrantedVia.Cli, expiresAt));

    public void Dispose()
    {
        Store.Dispose();
        temp.Dispose();
    }
}

/// <summary>Envelope checks for story 1.20's tests.</summary>
internal static class Grants
{
    public static JsonElement Ok(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        var root = result.StructuredContent.Value;
        Assert.False(result.IsError, root.GetRawText());
        TextBlockAssert.Matches(result);
        return root;
    }

    /// <summary>Asserts <c>ReadNotGranted</c>, retryable, naming the CLI, and no secret, name or path in the whole result.</summary>
    public static JsonElement NotGranted(CallToolResult result, string sectionId)
    {
        Assert.True(result.IsError, result.StructuredContent?.GetRawText());
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal("ReadNotGranted", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Contains("OneNoteMcp grant --read-section", error.GetProperty("userAction").GetString()!, StringComparison.Ordinal);
        Assert.Contains(sectionId, error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        AssertNothingLeaks(result);
        return error;
    }

    /// <summary>
    /// Asserts a <c>read_result</c> refusal because the grant that allowed the original call has ended (bug 1.22, R3):
    /// <c>ReadNotGranted</c>, not retryable for an expired <c>hours</c> grant (the default) and retryable for a revoked
    /// one, the lapsed userAction (grant again, then repeat the original call), the section's ID, and no secret, name or
    /// path in the whole result.
    /// </summary>
    public static JsonElement Lapsed(CallToolResult result, string sectionId, bool retryable = false)
    {
        Assert.True(result.IsError, result.StructuredContent?.GetRawText());
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal("ReadNotGranted", error.GetProperty("code").GetString());
        Assert.Equal(retryable, error.GetProperty("retryable").GetBoolean());
        Assert.Equal(ReadAccess.LapsedUserAction, error.GetProperty("userAction").GetString());
        Assert.Contains("repeat the original call", error.GetProperty("userAction").GetString()!, StringComparison.Ordinal);
        Assert.Contains(sectionId, error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        AssertNothingLeaks(result);
        return error;
    }

    /// <summary>Neither the protected text, the section's name in quotes, nor its path appears anywhere in the result.</summary>
    public static void AssertNothingLeaks(CallToolResult result)
    {
        var text = ((TextContentBlock)result.Content[0]).Text + result.StructuredContent?.GetRawText();
        Assert.DoesNotContain(VaultGateway.Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Vault page", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Vault.one", text, StringComparison.Ordinal);
    }

    public static IReadOnlyList<string> Withheld(JsonElement root) =>
        root.GetProperty("meta").TryGetProperty("withheld", out var w) ? [.. w.EnumerateArray().Select(e => e.GetString()!)] : [];

    public static IReadOnlyList<string> WarningCodes(JsonElement root) =>
        [.. root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()!)];

    public static string Xml(JsonElement root) => root.GetProperty("xml").GetString()!;
}
