using System.Runtime.InteropServices;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using Xunit;

namespace OneNoteMcp.Tests;

public sealed class ComGatewayTests
{
    [Fact]
    public async Task NotRunning_NeverCreatesCom_AndThrowsOneNoteNotRunning()
    {
        var created = 0;
        using var gateway = new ComGateway(new FixedProbe(false), () =>
        {
            Interlocked.Increment(ref created);
            throw new InvalidOperationException("COM must not be created when OneNote is not running.");
        });

        var ex = await Assert.ThrowsAsync<OneNoteException>(
            () => gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCode.OneNoteNotRunning, ex.Code);
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task ActivationComFailure_IsWrappedAsInternal_WithHResult()
    {
        using var gateway = new ComGateway(new FixedProbe(true), () => throw new COMException("fail", unchecked((int)0x80080005)));

        var ex = await Assert.ThrowsAsync<OneNoteException>(
            () => gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCode.Internal, ex.Code);
        Assert.Equal(unchecked((int)0x80080005), ex.ComHResult);
        Assert.Contains("0x80080005", ex.Message, StringComparison.Ordinal);
        Assert.IsType<COMException>(ex.InnerException);
    }

    [Fact]
    public async Task ServerUnavailable_DropsReference_SoNextCallRecreatesApplication()
    {
        var created = 0;
        var app = new FakeApplication(() => throw new COMException("gone", unchecked((int)0x800706BA)));
        using var gateway = new ComGateway(new FixedProbe(true), () =>
        {
            created++;
            return app;
        });

        var first = await Assert.ThrowsAsync<OneNoteException>(
            () => gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, TestContext.Current.CancellationToken));
        Assert.Equal(unchecked((int)0x800706BA), first.ComHResult);

        app.OnGetHierarchy = () => "<ok/>";
        var xml = await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, TestContext.Current.CancellationToken);

        Assert.Equal("<ok/>", xml);
        Assert.Equal(2, created);
    }

    [Fact]
    public async Task NonComException_FromCall_IsWrappedAsInternal_WithHResult()
    {
        var inner = new ArgumentException("bad arg");
        var app = new FakeApplication(() => throw inner);
        using var gateway = new ComGateway(new FixedProbe(true), () => app);

        var ex = await Assert.ThrowsAsync<OneNoteException>(
            () => gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCode.Internal, ex.Code);
        Assert.Equal(inner.HResult, ex.ComHResult);
        Assert.Contains($"0x{inner.HResult:X8}", ex.Message, StringComparison.Ordinal);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public async Task NonComException_FromActivation_IsWrappedAsInternal()
    {
        using var gateway = new ComGateway(new FixedProbe(true), () => throw new UnauthorizedAccessException("denied"));

        var ex = await Assert.ThrowsAsync<OneNoteException>(
            () => gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCode.Internal, ex.Code);
        Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
    }

    [Theory]
    [InlineData(HierarchyScope.Self, 0)]
    [InlineData(HierarchyScope.Children, 1)]
    [InlineData(HierarchyScope.Notebooks, 2)]
    [InlineData(HierarchyScope.Sections, 3)]
    [InlineData(HierarchyScope.Pages, 4)]
    public void MapScope_MatchesTypeLibraryValues(HierarchyScope scope, int expected) =>
        Assert.Equal(expected, (int)ComGateway.MapScope(scope));

    private sealed class FixedProbe(bool running) : IProcessProbe
    {
        public bool IsOneNoteRunning() => running;
    }
}
