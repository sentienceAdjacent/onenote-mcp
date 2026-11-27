using OneNoteMcp.Core.Writes;
using Xunit;

namespace OneNoteMcp.Tests;

public class ScreenTextTests
{
    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\u2028b")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u200Bb")]
    [InlineData("a\U000E0041b")]   // a tag character, outside the BMP
    [InlineData("a\U000E0001")]
    public void HasHidden_RefusesHiddenOrBrokenText(string text) => Assert.True(ScreenText.HasHidden(text));

    // Built in code: an attribute argument can't carry a lone surrogate (it becomes U+FFFD).
    [Fact]
    public void HasHidden_RefusesALoneSurrogate()
    {
        Assert.True(ScreenText.HasHidden("a" + (char)0xD800 + "b"));
        Assert.True(ScreenText.HasHidden("a" + (char)0xDC00));
    }

    [Theory]
    [InlineData("Plain title")]
    [InlineData("Notes \U0001F600 and é")]   // a visible character outside the BMP
    [InlineData("")]
    public void HasHidden_AcceptsVisibleText(string text) => Assert.False(ScreenText.HasHidden(text));
}
