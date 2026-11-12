using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>AD-17: only Xml parses OneNote's dates; a write's expected date becomes a UTC <see cref="DateTime"/>.</summary>
public sealed class OneNoteDatesTests
{
    [Theory]
    [InlineData("2026-11-11T10:20:30.000Z")]
    [InlineData("2026-11-11T10:20:30Z")]
    [InlineData("2026-11-11T12:20:30.000+02:00")]
    [InlineData("2026-11-11T05:20:30-05:00")]
    [InlineData("2026-11-11T10:20:30")] // no offset: UTC, never local
    public void ToUtc_ReadsOneNotesDate_AsUtc(string value)
    {
        var date = OneNoteDates.ToUtc(value);

        Assert.Equal(new DateTime(2026, 11, 11, 10, 20, 30, DateTimeKind.Utc), date);
        Assert.Equal(DateTimeKind.Utc, date!.Value.Kind);
    }

    [Fact]
    public void ToUtc_KeepsMilliseconds() =>
        Assert.Equal(new DateTime(2026, 11, 11, 10, 20, 30, 123, DateTimeKind.Utc), OneNoteDates.ToUtc("2026-11-11T10:20:30.123Z"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x")]
    [InlineData("2026-13-40T99:00:00Z")]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("0001-01-01T00:00:00.000Z")]
    public void ToUtc_IsNull_ForEmptyUnparsableOrMinimum(string? value) =>
        Assert.Null(OneNoteDates.ToUtc(value));

    [Theory]
    [InlineData("1899-12-30T00:00:00Z")] // OADate 0: OneNote reads it as "no date" and skips the check
    [InlineData("1899-12-30T02:00:00+02:00")] // the same instant
    [InlineData("1899-12-29T23:59:59Z")] // negative OADate
    [InlineData("1000-06-15T00:00:00Z")]
    [InlineData("0099-12-31T23:59:59Z")] // before the year 100: ToOADate overflows
    [InlineData("0050-01-01T00:00:00Z")]
    [InlineData("0001-01-02T00:00:00Z")]
    public void ToUtc_IsNull_OnOrBeforeTheOleEpoch(string value) =>
        Assert.Null(OneNoteDates.ToUtc(value));

    [Fact]
    public void ToUtc_JustAfterTheOleEpoch_IsADate_ThatConvertsToAPositiveOADate()
    {
        var date = OneNoteDates.ToUtc("1899-12-30T00:00:01Z");

        Assert.NotNull(date);
        Assert.True(date!.Value.ToOADate() > 0);
    }

    [Fact]
    public void ToUtc_EveryDateItReturns_ConvertsToAPositiveOADate()
    {
        foreach (var value in new[] { "1900-01-01T00:00:00Z", "2026-11-11T10:20:30.123Z", "9999-12-31T23:59:59Z" })
        {
            Assert.True(OneNoteDates.ToUtc(value)!.Value.ToOADate() > 0, value);
        }
    }
}
