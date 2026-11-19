using OneNoteMcp.Core.Writes;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 3.6: the settle before a failed cloud write's re-read (AD-7 Moves, 3.12 Q4), extracted unchanged from
/// <see cref="MoveContainerWrite"/> and <see cref="RestoreFromRecycleBinWrite"/> so the merge shares it.
/// </summary>
public sealed class CloudSettleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheSettleTime_IsTenSeconds_AsTheMoves()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), CloudSettle.Time);
    }

    [Fact]
    public async Task NoCloud_DoesntSettle()
    {
        var settles = new List<TimeSpan>();

        await CloudSettle.QuietlyAsync((t, _) => { settles.Add(t); return Task.CompletedTask; }, touchesCloud: false, Ct);

        Assert.Empty(settles);
    }

    [Fact]
    public async Task Cloud_SettlesTheSettleTime()
    {
        var settles = new List<TimeSpan>();

        await CloudSettle.QuietlyAsync((t, _) => { settles.Add(t); return Task.CompletedTask; }, touchesCloud: true, Ct);

        Assert.Equal([CloudSettle.Time], settles);
    }

    [Fact]
    public async Task ACancelOrTheDeadline_CutsItShort_Silently()
    {
        await CloudSettle.QuietlyAsync((_, _) => Task.FromCanceled(new CancellationToken(canceled: true)), touchesCloud: true, Ct);
    }
}
