namespace OneNoteMcp.Tests;

/// <summary>
/// Story 4.7: made-up cloud links and an open-notebooks hierarchy in the shapes spike 4.12 measured (`LinkShape`: a cloud
/// page link has %7B…%7D GUIDs and a base-path to the notebook; a notebook link is its folder with a trailing /). No real
/// path, account ID or name: placeholder notebooks only.
/// </summary>
internal static class CloudLinkKit
{
    public const string Host = "d.docs.live.net";
    public const string Root = "https://d.docs.live.net/0123456789abcdef/Documents/";
    public const string EncodedNotebook = Root + "MCP%20Test%20Cloud";

    public const string NotebookId = "{C1000000-0000-4000-8000-000000000001}{1}{B0}";
    public const string Notebook2Id = "{C1000000-0000-4000-8000-000000000002}{1}{B0}";
    public const string LocalNotebookId = "{C1000000-0000-4000-8000-000000000003}{1}{B0}";
    public const string BusinessNotebookId = "{C1000000-0000-4000-8000-000000000004}{1}{B0}";
    public const string SectionId = "{C2000000-0000-4000-8000-000000000001}{1}{B0}";
    public const string LockedSectionId = "{C2000000-0000-4000-8000-000000000002}{1}{B0}";
    public const string InnerSectionId = "{C2000000-0000-4000-8000-000000000003}{1}{B0}";
    public const string OtherSectionId = "{C2000000-0000-4000-8000-000000000004}{1}{B0}";
    public const string BusinessSectionId = "{C2000000-0000-4000-8000-000000000005}{1}{B0}";
    public const string GroupId = "{C3000000-0000-4000-8000-000000000001}{1}{B0}";
    public const string PageId = "{C2000000-0000-4000-8000-000000000001}{11}{E1000000000000000000000000000000000000000001}";
    public const string OtherPageId = "{C2000000-0000-4000-8000-000000000001}{11}{E1000000000000000000000000000000000000000002}";

    public const string PageGuid = "{66666666-7777-8888-9999-AAAAAAAAAAAA}";
    public const string OtherPageGuid = "{12345678-1234-1234-1234-123456789ABC}";

    /// <summary>A cloud page link as get_hyperlink_to_object returns it (spike 4.12's shape).</summary>
    public static string PageLink(string section = "Probes.one", string pageGuid = "%7B66666666-7777-8888-9999-AAAAAAAAAAAA%7D", string basePath = EncodedNotebook) =>
        "onenote:" + EncodedNotebook + "/" + section + "#Text%20Page&section-id=%7B11111111-2222-3333-4444-555555555555%7D&page-id=" + pageGuid + "&end&base-path=" + basePath;

    /// <summary>A paragraph link: an object-id and an index after the page-id.</summary>
    public const string ParagraphLink =
        "onenote:" + EncodedNotebook + "/Probes.one#Text%20Page&section-id=%7B11111111-2222-3333-4444-555555555555%7D&page-id=%7B66666666-7777-8888-9999-AAAAAAAAAAAA%7D&object-id=%7BBBBBBBBB-CCCC-DDDD-EEEE-FFFFFFFFFFFF%7D&10&end&base-path=" + EncodedNotebook;

    public const string SectionLink = "onenote:" + EncodedNotebook + "/Probes.one#section-id=%7B11111111-2222-3333-4444-555555555555%7D&end&base-path=" + EncodedNotebook;

    public const string NotebookLink = "onenote:" + EncodedNotebook + "/";

    public const string LocalLink = "onenote:///C:\\Users\\mcp-test\\AppData\\Local\\OneNoteMcp-Test\\MCP%20Test\\Fixtures.one#Fixture%20Image&section-id={549B015A-1AC5-424A-A76E-2BD619ADB9A0}&page-id={F39C5F02-A7F5-41D7-837C-25A1D8E3F2F7}&end";

    public const string WebLink = "https://onedrive.live.com/redir.aspx?cid=0123456789abcdef&page=edit&resid=0123456789ABCDEF!123";

    /// <summary>The open notebooks at Sections scope: MCP Test Cloud (plain path, a locked section, a group with a section), optionally MCP Test Cloud 2, a local MCP Test and a SharePoint notebook B.</summary>
    public static string Hierarchy(bool withSecond = true, string cloudPath = Root + "MCP Test Cloud/") =>
        "<?xml version=\"1.0\"?>\n<one:Notebooks xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\">"
        + $"<one:Notebook name=\"MCP Test Cloud\" nickname=\"MCP Test Cloud\" ID=\"{NotebookId}\" path=\"{cloudPath}\">"
        + $"<one:Section name=\"Probes\" ID=\"{SectionId}\" path=\"{Root}MCP Test Cloud/Probes.one\"/>"
        + $"<one:Section name=\"Vault\" ID=\"{LockedSectionId}\" path=\"{Root}MCP Test Cloud/Vault.one\" encrypted=\"true\" locked=\"true\"/>"
        + $"<one:SectionGroup name=\"Group\" ID=\"{GroupId}\" path=\"{Root}MCP Test Cloud/Group\">"
        + $"<one:Section name=\"Inner\" ID=\"{InnerSectionId}\" path=\"{Root}MCP Test Cloud/Group/Inner.one\"/></one:SectionGroup>"
        + "</one:Notebook>"
        + (withSecond
            ? $"<one:Notebook name=\"MCP Test Cloud 2\" nickname=\"MCP Test Cloud 2\" ID=\"{Notebook2Id}\" path=\"{Root}MCP Test Cloud 2/\">"
                + $"<one:Section name=\"Other\" ID=\"{OtherSectionId}\" path=\"{Root}MCP Test Cloud 2/Other.one\"/></one:Notebook>"
            : string.Empty)
        + $"<one:Notebook name=\"MCP Test\" nickname=\"MCP Test\" ID=\"{LocalNotebookId}\" path=\"C:\\Users\\mcp-test\\AppData\\Local\\OneNoteMcp-Test\\MCP Test\">"
        + "<one:Section name=\"Fixtures\" ID=\"{C2000000-0000-4000-8000-000000000009}{1}{B0}\" path=\"C:\\Users\\mcp-test\\AppData\\Local\\OneNoteMcp-Test\\MCP Test\\Fixtures.one\"/></one:Notebook>"
        + $"<one:Notebook name=\"Notebook B\" nickname=\"Notebook B\" ID=\"{BusinessNotebookId}\" path=\"https://example.sharepoint.com/sites/team/Shared Documents/Notebook B\">"
        + $"<one:Section name=\"Plans\" ID=\"{BusinessSectionId}\" path=\"https://example.sharepoint.com/sites/team/Shared Documents/Notebook B/Plans.one\"/></one:Notebook>"
        + "</one:Notebooks>";

    /// <summary>A section's own element, as GetHierarchy at self returns it; protected when <paramref name="encrypted"/>.</summary>
    public static string SectionSelf(string id, string name, bool encrypted = false) =>
        $"<?xml version=\"1.0\"?>\n<one:Section xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\" name=\"{name}\" ID=\"{id}\" path=\"{Root}MCP Test Cloud/{name}.one\"" + (encrypted ? " encrypted=\"true\"" : string.Empty) + "/>";
}
