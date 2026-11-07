using OneNoteMcp.Core;
using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>AD-20's result store: TTL after the last read, LRU past maxStoreMB (UTF-16 bytes), and a lone over-cap result.</summary>
public sealed class ResultStoreTests : IDisposable
{
    private const int MB = 1024 * 1024;

    private readonly FakeClock clock = new();
    private readonly ResultStore store;
    private ResultSettings settings = new(100_000, TtlMinutes: 10, MaxStoreMB: 16);

    public ResultStoreTests() => store = new ResultStore(clock, () => settings);

    public void Dispose() => store.Dispose();

    [Fact]
    public void Add_ThenGet_ReturnsTheEntry_AndExpiresAtIsTtlAfterTheRead()
    {
        var entry = Entry(10);

        var (added, _) = store.Add(entry);
        clock.Advance(TimeSpan.FromMinutes(4));
        var (got, expiresAt, _) = store.Get(entry.ResultId);

        Assert.Equal(clock.GetUtcNow() - TimeSpan.FromMinutes(4) + TimeSpan.FromMinutes(10), added);
        Assert.Same(entry, got);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromMinutes(10), expiresAt);
    }

    [Fact]
    public void Ttl_RunsFromTheLastRead_AndAnExpiredEntryIsResultExpired()
    {
        var entry = Entry(10);
        store.Add(entry);

        clock.Advance(TimeSpan.FromMinutes(9));
        store.Get(entry.ResultId);
        clock.Advance(TimeSpan.FromMinutes(9));
        store.Get(entry.ResultId);
        clock.Advance(TimeSpan.FromMinutes(10));

        var ex = Assert.Throws<OneNoteException>(() => store.Get(entry.ResultId));
        Assert.Equal(ErrorCode.ResultExpired, ex.Code);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.TotalBytes);
    }

    [Fact]
    public void Sweep_DropsExpiredEntries_WithoutACall()
    {
        var entry = Entry(10);
        store.Add(entry);
        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal([entry.ResultId], store.Sweep());
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void PastTheCap_TheLeastRecentlyReadGoFirst_CountedAsUtf16Bytes()
    {
        var a = Entry(3 * MB); // 6 MB each
        var b = Entry(3 * MB);
        var c = Entry(3 * MB);
        store.Add(a);
        store.Add(b);
        store.Get(a.ResultId); // same clock tick: recency still orders them

        var (_, change) = store.Add(c);

        Assert.Equal([b.ResultId], change.Evicted);
        Assert.Equal(a.Bytes + c.Bytes, store.TotalBytes);
        Assert.Equal(12L * MB + StoredResult.TreeBytes(a.Tree) + StoredResult.TreeBytes(c.Tree), store.TotalBytes);
        Assert.Throws<OneNoteException>(() => store.Get(b.ResultId));
        store.Get(a.ResultId);
        store.Get(c.ResultId);
    }

    [Fact]
    public void ASingleResultOverTheCap_IsStoredAlone()
    {
        var small = Entry(10);
        store.Add(small);

        var big = Entry(9 * MB); // 18 MB, over the 16 MB cap
        var (_, change) = store.Add(big);

        Assert.Equal([small.ResultId], change.Evicted);
        Assert.Equal(1, store.Count);
        Assert.Same(big, store.Get(big.ResultId).Entry);
    }

    [Fact]
    public void APiBasicThatIsTheBody_IsCountedOnce_AnotherIsCountedToo()
    {
        var body = new string('x', 100);
        var same = Entry(body, piBasic: body);
        var other = Entry(body, piBasic: new string('y', 50));

        Assert.Equal(200 + StoredResult.TreeBytes(same.Tree), same.Bytes);
        Assert.Equal(300 + StoredResult.TreeBytes(other.Tree), other.Bytes);
    }

    [Fact]
    public void TheSpanTree_IsCounted_PerNodePlusRefsAndLabels()
    {
        var tree = ElementSpans.Hierarchy(BigXml.Hierarchy(sections: 2, pages: 3));
        var expected = tree.Nodes.Sum(n => StoredResult.NodeBytes + (2L * (n.Ref.Length + (n.Label?.Length ?? 0))));

        Assert.Equal(expected, StoredResult.TreeBytes(tree));
        Assert.Equal(1 + 1 + 2 + 6, tree.Nodes.Count); // root, notebook, 2 sections, 6 pages
    }

    [Fact]
    public void SettingsAreReadAtEachCall()
    {
        var entry = Entry(10);
        store.Add(entry);
        clock.Advance(TimeSpan.FromMinutes(5));

        settings = settings with { TtlMinutes = 5 };

        Assert.Throws<OneNoteException>(() => store.Get(entry.ResultId));
    }

    private static StoredResult Entry(int chars) => Entry(new string('A', chars));

    private static StoredResult Entry(string body, string? piBasic = null)
    {
        var tree = ElementSpans.Text(body);
        var original = new ToolSuccess(null, new ToolMeta("c", Binary: body), []);
        return new StoredResult(Guid.NewGuid().ToString("N"), ResultKind.Text, tree, piBasic, original, new int[tree.Nodes.Count], ResultGrants.None);
    }
}
