using Microsoft.Extensions.Configuration;
using PrintLogApi.Email;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email;

public class EmailOptionsValidatorTests
{
    private const string Key32 = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY="; // 32 bytes

    private static EmailOptions Valid(bool enabled = true, bool dryRun = false) => new()
    {
        Enabled = enabled,
        DryRun = dryRun,
        PostalAddress = "PO Box 1, Somewhere, USA",
        FromAddress = "updates@mail.3dprintlog.com",
        WebBaseUrl = "https://www.3dprintlog.com",
        ApiBaseUrl = "https://api.3dprintlog.com",
        UnsubscribeSigningKeys = [Key32],
        SuppressionPeppers = [Key32],
        DefaultTimeZone = "America/Chicago",
    };

    private static string? Failure(EmailOptions o)
    {
        var result = new EmailOptionsValidator().Validate(null, o);
        return result.Failed ? result.FailureMessage : null;
    }

    [Fact]
    public void ValidOptions_Succeed() => Assert.Null(Failure(Valid()));

    [Fact]
    public void Disabled_WithEmptySecrets_Succeeds()
        => Assert.Null(Failure(new EmailOptions { Enabled = false, DryRun = false }));

    [Theory]
    [InlineData(nameof(EmailOptions.PostalAddress))]
    [InlineData(nameof(EmailOptions.FromAddress))]
    [InlineData(nameof(EmailOptions.WebBaseUrl))]
    [InlineData(nameof(EmailOptions.ApiBaseUrl))]
    public void Enabled_RequiresSetting(string property)
    {
        var o = Valid();
        typeof(EmailOptions).GetProperty(property)!.SetValue(o, "");
        Assert.Contains(property, Failure(o));
    }

    [Fact]
    public void DryRun_AlsoRequiresSecrets()
    {
        var o = Valid(enabled: false, dryRun: true);
        o.PostalAddress = "";
        Assert.Contains(nameof(EmailOptions.PostalAddress), Failure(o));
    }

    [Fact]
    public void Enabled_RequiresSigningKey()
    {
        var o = Valid();
        o.UnsubscribeSigningKeys = [];
        Assert.Contains(nameof(EmailOptions.UnsubscribeSigningKeys), Failure(o));
    }

    [Fact]
    public void Enabled_RejectsShortSigningKey()
    {
        var o = Valid();
        o.UnsubscribeSigningKeys = [Convert.ToBase64String(new byte[16])];
        Assert.Contains(nameof(EmailOptions.UnsubscribeSigningKeys), Failure(o));
    }

    [Fact]
    public void Enabled_RequiresPepper()
    {
        var o = Valid();
        o.SuppressionPeppers = [];
        Assert.Contains(nameof(EmailOptions.SuppressionPeppers), Failure(o));
    }

    [Fact]
    public void Enabled_RequiresResolvableDefaultZone()
    {
        var o = Valid();
        o.DefaultTimeZone = "Not/AZone";
        Assert.Contains(nameof(EmailOptions.DefaultTimeZone), Failure(o));
    }

    // Numeric checks apply regardless of the flags: a zero rate would divide by zero in the dispatcher.
    [Theory]
    [InlineData(nameof(EmailOptions.MaxSendsPerSecond), 0)]
    [InlineData(nameof(EmailOptions.BatchSize), 0)]
    [InlineData(nameof(EmailOptions.LocalSendHour), 24)]
    [InlineData(nameof(EmailOptions.LocalSendHour), -1)]
    [InlineData(nameof(EmailOptions.HoldoutPercent), 101)]
    [InlineData(nameof(EmailOptions.HoldoutPercent), -1)]
    public void NumericBounds(string property, int value)
    {
        var o = new EmailOptions();
        typeof(EmailOptions).GetProperty(property)!.SetValue(o, value);
        Assert.Contains(property, Failure(o));
    }

    [Theory]
    [InlineData(nameof(EmailOptions.EvaluatorInterval))]
    [InlineData(nameof(EmailOptions.DispatchInterval))]
    [InlineData(nameof(EmailOptions.FrequencyCapWindow))]
    public void Intervals_MustBePositive(string property)
    {
        var o = new EmailOptions();
        typeof(EmailOptions).GetProperty(property)!.SetValue(o, TimeSpan.Zero);
        Assert.Contains(property, Failure(o));
    }

    [Fact]
    public void NegativeDailyCap_Fails()
        => Assert.Contains(nameof(EmailOptions.DailyCap), Failure(new EmailOptions { DailyCap = -1 }));

    [Fact]
    public void IsCampaignEnabled_DefaultsFalse()
    {
        var o = new EmailOptions();
        o.Campaigns[EmailOptions.CampaignKey("monthly-recap")] = new CampaignOptions { Enabled = true };
        Assert.True(o.IsCampaignEnabled("monthly-recap"));
        Assert.False(o.IsCampaignEnabled("onboarding"));
    }

    [Fact]
    public void CampaignSwitch_BindsFromAnAppServiceSettingName()
    {
        // App Service on Linux rejects a setting name with a hyphen, so this is the only way
        // production can switch on a hyphenated campaign. The appsettings.json default sits
        // underneath it, as it does in the real host.
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Email:Campaigns:monthly_recap:Enabled"] = "true" })
            .Build();

        var o = config.GetSection(EmailOptions.SectionName).Get<EmailOptions>()!;

        Assert.True(o.IsCampaignEnabled("monthly-recap"));
        Assert.False(o.IsCampaignEnabled("printer-silent"));
        Assert.Null(Failure(o));
    }

    [Fact]
    public void HyphenatedCampaignKey_Fails()
    {
        var o = Valid();
        o.Campaigns["monthly-recap"] = new CampaignOptions { Enabled = true };
        Assert.Contains("Campaigns:monthly_recap", Failure(o));
    }
}
