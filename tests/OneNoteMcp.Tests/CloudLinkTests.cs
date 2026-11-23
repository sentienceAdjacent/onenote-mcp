using OneNoteMcp.Core;
using OneNoteMcp.Core.AppState;
using Xunit;
using K = OneNoteMcp.Tests.CloudLinkKit;
using R = OneNoteMcp.Core.AppState.CloudLinks.Rules;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 4.7: <see cref="CloudLinks"/>, step 1's link rules and step 3's match against the open notebooks, on strings only
/// (spine AD-7 <c>AppState</c>, <c>navigate_to_url</c>).
/// </summary>
public sealed class CloudLinkTests
{
    // --- Step 1 -------------------------------------------------------------------------------------------------

    [Fact]
    public void ACloudPageLink_IsAPageLink_WithItsPageGuid_AndBasePath()
    {
        Assert.Null(CloudLinks.Parse(K.PageLink(), out var link));

        Assert.Equal(CloudLinkKind.Page, link!.Kind);
        Assert.Equal(K.Host, link.Host);
        Assert.Equal(["0123456789abcdef", "Documents", "MCP Test Cloud", "Probes.one"], link.Segments);
        Assert.Equal(Guid.Parse(K.PageGuid), link.PageGuid);
        Assert.Equal(["0123456789abcdef", "Documents", "MCP Test Cloud"], link.BasePathSegments);
    }

    [Fact]
    public void AParagraphLink_IsAPageLink()
    {
        Assert.Null(CloudLinks.Parse(K.ParagraphLink, out var link));
        Assert.Equal(CloudLinkKind.Page, link!.Kind);
    }

    [Fact]
    public void ASectionLink_AndANotebookLink_AreRead()
    {
        Assert.Null(CloudLinks.Parse(K.SectionLink, out var section));
        Assert.Equal((CloudLinkKind.Section, (Guid?)null), (section!.Kind, section.PageGuid));

        Assert.Null(CloudLinks.Parse(K.NotebookLink, out var notebook));
        Assert.Equal(CloudLinkKind.Notebook, notebook!.Kind);
        Assert.Equal(["0123456789abcdef", "Documents", "MCP Test Cloud"], notebook.Segments);
    }

    [Fact]
    public void BracedGuids_AndAPlainNotebookPath_AreReadToo()
    {
        var plain = "onenote:" + K.Root + "MCP Test Cloud/Probes.one#Text Page&page-id=" + K.PageGuid + "&end";

        Assert.Null(CloudLinks.Parse(plain, out var link));
        Assert.Equal("MCP Test Cloud", link!.Segments[2]);
        Assert.Equal(Guid.Parse(K.PageGuid), link.PageGuid);
    }

    public static TheoryData<string, string> Refused() => new()
    {
        { string.Empty, R.Empty },
        { "   ", R.Empty },
        { K.WebLink, R.NotOneNote },
        { "mailto:someone@example.com", R.NotOneNote },
        { "file:///C:/x.one", R.NotOneNote },
        { K.LocalLink, R.LocalLink },
        { "onenote:\\\\server\\share\\Notes.one#x&page-id=" + K.PageGuid, R.LocalLink },
        { "onenote:#Text%20Page&page-id=" + K.PageGuid + "&end", R.LocalLink },
        { "onenote:///C:/Users/x/Notes.one", R.LocalLink },
        { "ONENOTE:C:/x", R.LocalLink },
        { "onenote:" + K.EncodedNotebook + "/Pro\nbes.one", R.ControlCharacters },
        { "onenote:" + K.EncodedNotebook + "\x200B/Probes.one", R.ControlCharacters },
        { "onenote:" + K.EncodedNotebook + "/Pro%0Abes.one", R.ControlCharacters },
        { "onenote:" + K.EncodedNotebook + "/x\\Probes.one", R.Backslash },
        { "onenote:" + K.EncodedNotebook + "/x%5CProbes.one", R.Backslash },
        { "onenote:" + K.EncodedNotebook + "/Probes.one?x=1#Text&page-id=" + K.PageGuid, R.Query },
        { "onenote:" + K.EncodedNotebook + "/Probes.one#Text&page-id=" + K.PageGuid + "&foo=bar", R.FragmentParameter },
        { "onenote:" + K.EncodedNotebook + "/Probes.one#Text&page-id=" + K.PageGuid + "&evil", R.FragmentParameter },
        { "onenote:" + K.EncodedNotebook + "/Probes.one#Text&page-id=" + K.PageGuid + "&page-id=" + K.PageGuid, R.FragmentParameter },
        { "onenote:" + K.EncodedNotebook + "/Probes.one#Text&page-id=66666666-7777-8888-9999-AAAAAAAAAAAA", R.FragmentParameter },
        { "onenote:" + K.EncodedNotebook + "/Probes.one#Text&object-id=" + K.PageGuid, R.FragmentParameter },
        { "onenote:" + K.EncodedNotebook + "/#page-id=" + K.PageGuid, R.FragmentParameter },
        { "onenote:" + K.Root + "MCP%2520Test%2520Cloud/Probes.one#Text&page-id=" + K.PageGuid, R.Encoding },
        { "onenote:" + K.EncodedNotebook + "/Probes.one#Text&page-id=%257B66666666-7777-8888-9999-AAAAAAAAAAAA%257D", R.Encoding },
        { "onenote:" + K.EncodedNotebook + "/Probes.one#Text%2523&end", R.Encoding },
        { "onenote:" + K.Root + "MCP%252525252525Test/Probes.one", R.Encoding },
        { "onenote:" + K.EncodedNotebook + "/a%2Fb/Probes.one", R.Encoding },
        { "onenote:" + K.EncodedNotebook + "/../Probes.one", R.Encoding },
        { "onenote:" + K.EncodedNotebook + "//Probes.one", R.Encoding },
        { "onenote:https://user@d.docs.live.net/x/Probes.one", R.Encoding },
        { "onenote:" + K.EncodedNotebook + "/Probes.one#Text&page-id=" + K.PageGuid + "&base-path=C:/Users", R.BasePath },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Step1_Refuses_WithTheRule_AndAMessageThatNamesNoPartOfTheLink(string url, string rule)
    {
        var refusal = CloudLinks.Parse(url, out var link);

        Assert.NotNull(refusal);
        Assert.Null(link);
        Assert.Equal(rule, refusal.Rule);
        Assert.False(string.IsNullOrWhiteSpace(refusal.UserAction));
        Assert.DoesNotContain("Probes", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docs.live.net", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MCP Test", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ALocalLink_SaysToCallNavigateToWithTheObjectsId()
    {
        var refusal = CloudLinks.Parse(K.LocalLink, out _);

        Assert.Contains("call navigate_to with the object's ID", refusal!.UserAction, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", CloudLinks.Kinds.Empty)]
    [InlineData("https://onedrive.live.com/x", CloudLinks.Kinds.Web)]
    [InlineData("onenote:///C:\\x.one", CloudLinks.Kinds.Local)]
    [InlineData("onenote:https://d.docs.live.net/x", CloudLinks.Kinds.Cloud)]
    [InlineData("mailto:x", CloudLinks.Kinds.Other)]
    public void KindOf_ForTheLog(string url, string kind) =>
        Assert.Equal(kind, CloudLinks.KindOf(url));

    // --- Step 3 -------------------------------------------------------------------------------------------------

    [Fact]
    public void APageLink_MatchesItsSection_InTheOpenNotebook_WithAPlainPath()
    {
        var target = Match(K.PageLink());

        Assert.Equal(new CloudLinkTarget(CloudLinkKind.Page, K.NotebookId, K.SectionId, false, Guid.Parse(K.PageGuid)), target);
    }

    [Fact]
    public void ANotebookReportedPercentEncoded_StillMatches()
    {
        var target = Match(K.PageLink(), K.Hierarchy(cloudPath: K.EncodedNotebook + "/"));

        Assert.Equal(K.NotebookId, target.NotebookId);
    }

    [Fact]
    public void APlainLink_MatchesToo()
    {
        var target = Match("onenote:" + K.Root + "MCP Test Cloud/Probes.one#section-id=%7B11111111-2222-3333-4444-555555555555%7D&end");

        Assert.Equal((CloudLinkKind.Section, K.SectionId), (target.Kind, target.SectionId));
    }

    [Fact]
    public void HostAndSegments_CompareIgnoringCase()
    {
        var target = Match("onenote:https://D.DOCS.LIVE.NET/0123456789ABCDEF/documents/mcp%20test%20cloud/PROBES.ONE#section-id=%7B11111111-2222-3333-4444-555555555555%7D&end");

        Assert.Equal(K.SectionId, target.SectionId);
    }

    [Fact]
    public void ASectionInASectionGroup_Matches()
    {
        var target = Match("onenote:" + K.EncodedNotebook + "/Group/Inner.one#Text&page-id=" + K.PageGuid + "&end&base-path=" + K.EncodedNotebook);

        Assert.Equal((CloudLinkKind.Page, K.NotebookId, K.InnerSectionId), (target.Kind, target.NotebookId, target.SectionId));
    }

    [Fact]
    public void ALockedSection_IsMatched_AndMarkedLocked()
    {
        var target = Match(K.PageLink("Vault.one"));

        Assert.Equal((K.LockedSectionId, true), (target.SectionId, target.SectionLocked));
    }

    [Fact]
    public void ANotebookLink_MatchesTheNotebook()
    {
        Assert.Equal(new CloudLinkTarget(CloudLinkKind.Notebook, K.NotebookId, null, false, null), Match(K.NotebookLink));
        Assert.Equal(K.NotebookId, Match("onenote:" + K.EncodedNotebook).NotebookId);
    }

    [Fact]
    public void ASegmentBoundaryNearMiss_IsNotInsideTheNotebook()
    {
        // MCP Test Cloud is open, MCP Test Cloud 2 isn't: a link into the second must not match the first.
        var link = "onenote:" + K.Root + "MCP%20Test%20Cloud%202/Other.one#section-id=%7B11111111-2222-3333-4444-555555555555%7D&end";

        Assert.Equal(R.NotInOpenNotebook, Refusal(link, K.Hierarchy(withSecond: false)).Rule);
        Assert.Equal((K.Notebook2Id, K.OtherSectionId), (Match(link).NotebookId, Match(link).SectionId));
    }

    [Fact]
    public void ABusinessNotebook_IsMatchedOnItsOwnHost()
    {
        // Decision OQ2: a link into any open cloud notebook's host is accepted.
        var target = Match("onenote:https://example.sharepoint.com/sites/team/Shared%20Documents/Notebook%20B/Plans.one#section-id=%7B11111111-2222-3333-4444-555555555555%7D&end");

        Assert.Equal((K.BusinessNotebookId, K.BusinessSectionId), (target.NotebookId, target.SectionId));
    }

    public static TheoryData<string, string> Unmatched() => new()
    {
        { "onenote:https://evil.example/0123456789abcdef/Documents/MCP%20Test%20Cloud/Probes.one#section-id=%7B11111111-2222-3333-4444-555555555555%7D", R.NotInOpenNotebook },
        { "onenote:" + K.Root + "Closed%20Notebook/Probes.one#section-id=%7B11111111-2222-3333-4444-555555555555%7D", R.NotInOpenNotebook },
        { "onenote:" + K.Root, R.NotInOpenNotebook },
        { "onenote:https://d.docs.live.net", R.NotInOpenNotebook },
        { K.PageLink(basePath: K.Root + "MCP%20Test%20Cloud%202"), R.BasePath },
        { K.PageLink(basePath: K.EncodedNotebook + "/Group"), R.BasePath },
        { K.PageLink(basePath: "https://evil.example/0123456789abcdef/Documents/MCP%20Test%20Cloud"), R.BasePath },
        { K.PageLink("Missing.one"), R.NotASection },
        { K.PageLink("Group/Missing.one"), R.NotASection },
        { "onenote:" + K.EncodedNotebook + "/Group/", R.SectionGroupFolder },
        { "onenote:" + K.EncodedNotebook + "/Elsewhere/", R.NotTheNotebook },
    };

    [Theory]
    [MemberData(nameof(Unmatched))]
    public void Step3_Refuses_WithTheRule(string url, string rule)
    {
        var refusal = Refusal(url, K.Hierarchy());

        Assert.Equal(rule, refusal.Rule);
        Assert.DoesNotContain("Probes", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MCP Test", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASectionGroupsFolder_SaysToCallNavigateToWithTheGroupsId()
    {
        var refusal = Refusal("onenote:" + K.EncodedNotebook + "/Group/", K.Hierarchy());

        Assert.Equal(CloudLinks.SectionGroupFolderUserAction, refusal.UserAction);
        Assert.Contains("call navigate_to with the group's ID", refusal.UserAction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(K.GroupId, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableHierarchy_IsInvalidXml()
    {
        Assert.Null(CloudLinks.Parse(K.PageLink(), out var link));

        var ex = Assert.Throws<OneNoteException>(() => CloudLinks.Match(link!, "<not xml", out _));
        Assert.Equal(ErrorCode.InvalidXml, ex.Code);
    }

    // --- The landing check's page-id ----------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PageGuidOf_ReadsCloudAndLocalLinks(bool cloud)
    {
        var link = cloud ? K.PageLink() : K.LocalLink;
        var expected = cloud ? Guid.Parse(K.PageGuid) : Guid.Parse("{F39C5F02-A7F5-41D7-837C-25A1D8E3F2F7}");

        Assert.Equal(expected, CloudLinks.PageGuidOf(link));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("onenote:https://d.docs.live.net/x/Probes.one#section-id=%7B11111111-2222-3333-4444-555555555555%7D&end")]
    [InlineData("onenote:https://d.docs.live.net/x/Probes.one#page-id=nope")]
    public void PageGuidOf_IsNull_WithoutAPageId(string? link) =>
        Assert.Null(CloudLinks.PageGuidOf(link));

    private static CloudLinkTarget Match(string url, string? hierarchy = null)
    {
        Assert.Null(CloudLinks.Parse(url, out var link));
        Assert.Null(CloudLinks.Match(link!, hierarchy ?? K.Hierarchy(), out var target));
        return target!;
    }

    private static CloudLinkRefusal Refusal(string url, string hierarchy)
    {
        Assert.Null(CloudLinks.Parse(url, out var link));
        var refusal = CloudLinks.Match(link!, hierarchy, out var target);
        Assert.Null(target);
        return refusal!;
    }
}
