using System.Text;
using OneNoteMcp.Core;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>Story 6: <see cref="FakeGateway"/> serves every port method from the captured fixtures (AD-16), as OneNote would.</summary>
public sealed class FakeGatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Entries()
    {
        var data = new TheoryData<string>();
        foreach (var entry in FakeGateway.Manifest.Entries)
        {
            data.Add(entry.Key);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public async Task EachEntry_IsServedByteForByte_OrThrowsItsError(string key)
    {
        var entry = FakeGateway.Manifest.Entries.Single(e => e.Key == key);
        var call = Call(new FakeGateway(), entry.Method, [.. entry.Args]);

        if (entry.Error is { } error)
        {
            var ex = await Assert.ThrowsAsync<OneNoteException>(call);
            Assert.Equal(error.Code, ex.Code.ToString());
            Assert.Equal(error.Message, ex.Message);
            Assert.Equal(error.HResult, ex.ComHResult is { } hr ? $"0x{unchecked((uint)hr):X8}" : null);
        }
        else
        {
            var served = await call();
            Assert.Equal(File.ReadAllBytes(Path.Combine(FakeGateway.DefaultDirectory, entry.File!)), new UTF8Encoding(false).GetBytes(served));
        }
    }

    [Fact]
    public void Manifest_CoversAllNineContentPortMethods_WithASuccess()
    {
        // NavigateTo (story 1.21) returns no content, and CreateNewPage (story 2.4), CreateSection (story 2.11), the
        // section-group and notebook creates (story 5.3), the page writes (story 2.7) and UpdateHierarchy (story 5.4) are writes, so none has a fixture;
        // FakeGateway records them instead.
        string[] writes =
        [
            nameof(IOneNoteGateway.CreateNewPageAsync),
            nameof(IOneNoteGateway.CreateSectionAsync),
            nameof(IOneNoteGateway.CreateSectionGroupAsync),
            nameof(IOneNoteGateway.CreateNotebookAsync),
        ];
        var methods = typeof(IOneNoteGateway).GetMethods()
            .Where(m => m.ReturnType == typeof(Task<string>) && !writes.Contains(m.Name))
            .Select(m => m.Name[..^"Async".Length])
            .ToHashSet();
        Assert.Equal(9, methods.Count);
        Assert.Equal(19, typeof(IOneNoteGateway).GetMethods().Length); // + UpdateHierarchy (story 5.4) and DeleteHierarchy (story 5.5), Tasks with no content

        var served = FakeGateway.Manifest.Entries.Where(e => e.Error is null).Select(e => e.Method).ToHashSet();

        Assert.Subset(served, methods);
        Assert.Superset(methods, served);
    }

    [Fact]
    public void Manifest_EveryFileExists_AndEveryFileIsListed()
    {
        var listed = FakeGateway.Manifest.Entries.Select(e => e.File).OfType<string>()
            .Append(FakeGateway.Manifest.Probe?.BeforeFile)
            .OfType<string>()
            .Append(FixtureManifest.FileName)
            .ToHashSet(StringComparer.Ordinal);
        var onDisk = Directory.EnumerateFiles(FakeGateway.DefaultDirectory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(FakeGateway.DefaultDirectory, f).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(listed.Order(StringComparer.Ordinal), onDisk.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Manifest_KeysAreUnique()
    {
        var keys = FakeGateway.Manifest.Entries.Select(e => e.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("GetHierarchy", new[] { "{00000000-0000-0000-0000-000000000000}{1}{B0}", "Pages" })]
    [InlineData("GetHierarchyParent", new[] { "{00000000-0000-0000-0000-000000000000}{1}{B0}" })]
    [InlineData("GetPageContent", new[] { "{00000000-0000-0000-0000-000000000000}{1}{B0}", "Basic" })]
    [InlineData("GetBinaryPageContent", new[] { "{00000000-0000-0000-0000-000000000000}{1}{B0}", "{00000000-0000-0000-0000-000000000000}{1}{B0}" })]
    [InlineData("FindPages", new[] { "{00000000-0000-0000-0000-000000000000}{1}{B0}", "x", "true" })]
    [InlineData("FindMeta", new[] { "{00000000-0000-0000-0000-000000000000}{1}{B0}", "x", "true" })]
    [InlineData("GetHyperlinkToObject", new[] { "{00000000-0000-0000-0000-000000000000}{1}{B0}", "" })]
    [InlineData("GetWebHyperlinkToObject", new[] { "{00000000-0000-0000-0000-000000000000}{1}{B0}", "" })]
    public async Task UnknownId_IsObjectNotFound(string method, string[] args)
    {
        var ex = await Assert.ThrowsAsync<OneNoteException>(Call(new FakeGateway(), method, args));

        Assert.Equal(ErrorCode.ObjectNotFound, ex.Code);
        Assert.Equal(unchecked((int)0x80042014), ex.ComHResult);
    }

    [Fact]
    public async Task HierarchyXml_WhenSet_AnswersEveryGetHierarchy()
    {
        var gateway = new FakeGateway { HierarchyXml = "<x />" };

        Assert.Equal("<x />", await gateway.GetHierarchyAsync("{anything}", HierarchyScope.Self, Ct));
        Assert.Equal([("{anything}", HierarchyScope.Self)], gateway.Calls);
    }

    [Fact]
    public async Task Failure_WhenSet_IsThrownByEveryMethod()
    {
        var failure = new OneNoteException(ErrorCode.ComBusy, "busy");
        var gateway = new FakeGateway { Failure = failure, HierarchyXml = "<x />" };

        Assert.Same(failure, await Assert.ThrowsAsync<OneNoteException>(() => gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct)));
        Assert.Same(failure, await Assert.ThrowsAsync<OneNoteException>(() => gateway.GetSpecialLocationAsync(SpecialLocation.BackUpFolder, Ct)));
    }

    private static Func<Task<string>> Call(FakeGateway gateway, string method, string[] args) => method switch
    {
        "GetHierarchy" => () => gateway.GetHierarchyAsync(args[0], Enum.Parse<HierarchyScope>(args[1]), Ct),
        "GetHierarchyParent" => () => gateway.GetHierarchyParentAsync(args[0], Ct),
        "GetPageContent" => () => gateway.GetPageContentAsync(args[0], Enum.Parse<PageInfo>(args[1]), Ct),
        "GetBinaryPageContent" => () => gateway.GetBinaryPageContentAsync(args[0], args[1], Ct),
        "FindPages" => () => gateway.FindPagesAsync(args[0], args[1], bool.Parse(args[2]), Ct),
        "FindMeta" => () => gateway.FindMetaAsync(args[0], args[1], bool.Parse(args[2]), Ct),
        "GetHyperlinkToObject" => () => gateway.GetHyperlinkToObjectAsync(args[0], args[1], Ct),
        "GetWebHyperlinkToObject" => () => gateway.GetWebHyperlinkToObjectAsync(args[0], args[1], Ct),
        "GetSpecialLocation" => () => gateway.GetSpecialLocationAsync(Enum.Parse<SpecialLocation>(args[0]), Ct),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Not a port method."),
    };
}
