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
