using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

public class PushPreferenceTests
{
    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    [InlineData("  false  ")]
    public void IsEnabled_ReturnsFalse_ForCanonicalDisabledValues(string value)
        => Assert.False(PushPreference.IsEnabled(value));

    // "0" is deliberately enabled: only the exact token `false` disables. An unrecognised
    // value must never silently mute a user who never opted out.
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("  true ")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yes")]
    [InlineData("0")]
    [InlineData("garbage")]
    public void IsEnabled_ReturnsTrue_ForEverythingElse(string? value)
        => Assert.True(PushPreference.IsEnabled(value));
}
