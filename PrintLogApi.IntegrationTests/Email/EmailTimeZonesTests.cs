using PrintLogApi.Email;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailTimeZonesTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    [Fact]
    public void For_MissingUserZone_UsesDefault()
        => Assert.Equal(Chicago.BaseUtcOffset, EmailTimeZones.For(null, "America/Chicago").BaseUtcOffset);

    [Fact]
    public void For_InvalidUserZone_UsesDefault()
        => Assert.Equal(Chicago.BaseUtcOffset, EmailTimeZones.For("Not/AZone", "America/Chicago").BaseUtcOffset);

    [Fact]
    public void For_IanaUserZone_IsHonored()
        => Assert.Equal(TimeSpan.FromHours(12), EmailTimeZones.For("Pacific/Auckland", "America/Chicago").BaseUtcOffset);

    [Fact]
    public void For_WindowsUserZone_IsHonored()
        => Assert.Equal(TimeSpan.FromHours(-5), EmailTimeZones.For("Eastern Standard Time", "America/Chicago").BaseUtcOffset);

    [Fact]
    public void For_InvalidUserAndDefault_FallsBackToUtc()
        => Assert.Equal(TimeZoneInfo.Utc.BaseUtcOffset, EmailTimeZones.For(null, "Bad/Default").BaseUtcOffset);

    // 2026-03-08 02:00 does not exist in Chicago (spring forward); it is shifted to 03:00 CDT.
    [Fact]
    public void AtLocal_InSpringForwardGap_ShiftsForward()
        => Assert.Equal(
            new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero),
            EmailTimeZones.AtLocal(new DateOnly(2026, 3, 8), 2, Chicago).ToUniversalTime());

    [Fact]
    public void AtLocal_NormalDay()
        => Assert.Equal(
            new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero),
            EmailTimeZones.AtLocal(new DateOnly(2026, 11, 2), 9, Chicago).ToUniversalTime());

    // 2026-11-01 is the fall-back day: 9am CST is 15:00Z.
    [Fact]
    public void NextLocalHour_BeforeTheHour_IsToday()
        => Assert.Equal(
            new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero),
            EmailTimeZones.NextLocalHour(new DateTimeOffset(2026, 11, 1, 14, 59, 0, TimeSpan.Zero), 9, Chicago).ToUniversalTime());

    [Fact]
    public void NextLocalHour_AfterTheHour_IsTomorrow()
        => Assert.Equal(
            new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero),
            EmailTimeZones.NextLocalHour(new DateTimeOffset(2026, 11, 1, 15, 1, 0, TimeSpan.Zero), 9, Chicago).ToUniversalTime());
}
