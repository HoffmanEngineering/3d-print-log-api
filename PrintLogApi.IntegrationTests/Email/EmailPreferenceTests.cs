using PrintLogApi.Email;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailPreferenceTests
{
    // Absent means on: the owner chose opt-out, so a user who never touched the setting is emailed.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("true")]
    [InlineData(" TRUE ")]
    public void Parse_Enabled(string? value) => Assert.Equal(EmailPreferenceValue.Enabled, EmailPreference.Parse(value));

    [Theory]
    [InlineData("false")]
    [InlineData(" False ")]
    public void Parse_Disabled(string value) => Assert.Equal(EmailPreferenceValue.Disabled, EmailPreference.Parse(value));

    // Unlike push, a malformed value is never read as consent.
    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("garbage")]
    public void Parse_Unrecognized(string value) => Assert.Equal(EmailPreferenceValue.Unrecognized, EmailPreference.Parse(value));

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    public void IsEnabled_OnlyForEnabled(string? value, bool expected) => Assert.Equal(expected, EmailPreference.IsEnabled(value));
}
