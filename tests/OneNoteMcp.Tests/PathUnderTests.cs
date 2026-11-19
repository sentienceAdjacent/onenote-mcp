using OneNoteMcp.Core.Writes;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Entry 15: the one "under a root" test, shared by exports (AD-11) and the protected-by-path rule (AD-21): a path-segment
/// boundary, case-insensitive, separators and trailing separators normalised, a root that doesn't exist yet still counting,
/// and a URL never matching.
/// </summary>
public sealed class PathUnderTests
{
    [Theory]
    [InlineData(@"C:\exports\x.one", @"C:\exports")]
    [InlineData(@"C:\exports\sub\deeper\x.one", @"C:\exports")]
    [InlineData(@"c:\EXPORTS\x.one", @"C:\exports\")]
    [InlineData(@"C:/exports/x.one", @"C:\exports")]
    [InlineData(@"C:\exports\x.one", @"C:/Exports/")]
    [InlineData(@"C:\does-not-exist-yet-entry15\a\x.one", @"C:\does-not-exist-yet-entry15")]
    [InlineData(@"Z:\mapped\x.one", @"Z:\mapped")]
    public void APathInsideTheRoot_IsUnderIt(string path, string root) =>
        Assert.True(PathUnder.IsUnder(path, [root]));

    [Theory]
    [InlineData(@"C:\exports2\x.one", @"C:\exports")]
    [InlineData(@"C:\exportsx.one", @"C:\exports")]
    [InlineData(@"C:\exports", @"C:\exports")]
    [InlineData(@"C:\other\x.one", @"C:\exports")]
    [InlineData(@"https://d.docs.live.net/abc/exports/x.one", @"C:\exports")]
    [InlineData(@"relative\x.one", @"C:\exports")]
    [InlineData("", @"C:\exports")]
    public void APathOutsideTheRoot_IsNotUnderIt(string path, string root) =>
        Assert.False(PathUnder.IsUnder(path, [root]));

    [Fact]
    public void ARootThatIsNotALocalPath_ProtectsNothing_AndDoesNotThrow()
    {
        Assert.False(PathUnder.IsUnder(@"C:\exports\x.one", ["", "   ", "not a path", "https://x/y"]));
    }

    [Fact]
    public void AnyRootOfSeveral_Matches()
    {
        Assert.True(PathUnder.IsUnder(@"D:\backups\b.one", [@"C:\exports", @"D:\backups"]));
    }
}
