using PrintLogApi.Email;
using PrintLogApi.Email.Campaigns;
using PrintLogApi.Email.Templates;
using PrintLogApi.Email.Tokens;
using PrintLogApi.IntegrationTests.Email.Golden;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email.Campaigns;

/// <summary>The "a device was paired" security notice (api#287, item 1).</summary>
public class DevicePairedCampaignTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly DateTimeOffset PairedAt = new(2026, 10, 30, 20, 4, 0, TimeSpan.Zero);

    private readonly CustomWebApplicationFactory _factory;

    public DevicePairedCampaignTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DevicePairedCampaign Campaign(IServiceScope scope)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(new EmailOptions
        {
            WebBaseUrl = "https://www.3dprintlog.test",
            ApiBaseUrl = "https://api.3dprintlog.test",
            PostalAddress = "PO Box 0, Testville, USA",
        });
        var sp = scope.ServiceProvider;
        var links = new EmailLinkBuilder(wrapped);
        return new DevicePairedCampaign(
            sp.GetRequiredService<PrintLogContext>(),
            sp.GetRequiredService<IEmailTemplateRenderer>(),
            new EmailFooterFactory(links, sp.GetRequiredService<IEmailTokenService>(), wrapped),
            links);
    }

    private static EmailOutbox Row(long userId, bool repaired = false, string deviceName = "printlog-bridge on voron", string? ipAddress = "203.0.113.7")
    {
        var row = DevicePairedCampaign.Queue(userId, Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), deviceName,
            "connections:write prints:write printers:slots", PairedAt, repaired, ipAddress);
        row.Id = 7;
        return row;
    }

    [Fact]
    public void Contract()
    {
        using var scope = _factory.Services.CreateScope();
        var campaign = Campaign(scope);

        Assert.Equal("device-paired", campaign.Name);
        Assert.True(campaign.IsSecurityNotice);
        Assert.False(campaign.CountsTowardFrequencyCap);
        Assert.True(campaign.ExemptFromFrequencyCap);
    }

    [Fact]
    public async Task Render_ShowsTheDeviceTheTimeInTheUsersZoneThePermissionsAndTheRevokeLink()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await EmailTestData.CreateUserAsync(db);
        await CampaignTestData.SetZoneAsync(db, user.Id, "America/Chicago");

        var rendered = await Campaign(scope).RenderAsync(Row(user.Id), Ct);

        Assert.NotNull(rendered);
        Assert.Equal("A new device was paired with your 3D Print Log account", rendered.Subject);
        Assert.Contains("printlog-bridge on voron", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("October 30, 2026 at 3:04 PM (America/Chicago)", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("Log prints, update printer slots, send heartbeats", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("https://www.3dprintlog.test/settings?", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("#connected-agents", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("printlog-bridge on voron", rendered.Html, StringComparison.Ordinal);
        Assert.Contains("From IP address: 203.0.113.7", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("203.0.113.7", rendered.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.7", rendered.Subject, StringComparison.Ordinal);
        Assert.Contains("If you don't recognize it, revoke it", rendered.Text, StringComparison.Ordinal);

        // A security notice has no unsubscribe link: opting out of it isn't offered.
        Assert.DoesNotContain("Unsubscribe", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Unsubscribe", rendered.Html, StringComparison.Ordinal);
        Assert.Contains("Manage email preferences", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_LabelsUtc_WhenTheUsersZoneIsUnknown()
    {
        using var scope = _factory.Services.CreateScope();
        var user = await EmailTestData.CreateUserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>());

        var rendered = await Campaign(scope).RenderAsync(Row(user.Id, repaired: true), Ct);

        Assert.NotNull(rendered);
        Assert.Equal("A device was paired again with your 3D Print Log account", rendered.Subject);
        Assert.Contains("October 30, 2026 at 8:04 PM UTC", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_EncodesTheDeviceName_WhichTheDeviceChose()
    {
        using var scope = _factory.Services.CreateScope();
        var user = await EmailTestData.CreateUserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>());

        var rendered = await Campaign(scope).RenderAsync(Row(user.Id, deviceName: "<a href=\"https://evil.test\">verify</a>"), Ct);

        Assert.NotNull(rendered);
        Assert.DoesNotContain("<a href=\"https://evil.test\"", rendered.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", rendered.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_LeavesTheAddressOut_WhenItWasUnknown()
    {
        using var scope = _factory.Services.CreateScope();
        var user = await EmailTestData.CreateUserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>());

        var rendered = await Campaign(scope).RenderAsync(Row(user.Id, ipAddress: null), Ct);

        Assert.NotNull(rendered);
        Assert.DoesNotContain("IP address", rendered.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("IP address", rendered.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_IsNull_WithoutASnapshot()
    {
        using var scope = _factory.Services.CreateScope();
        var user = await EmailTestData.CreateUserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>());
        var row = Row(user.Id);
        row.Exposure = null;

        Assert.Null(await Campaign(scope).RenderAsync(row, Ct));
    }

    [Fact]
    public async Task Golden()
    {
        var renderer = _factory.Services.GetRequiredService<IEmailTemplateRenderer>();
        var (html, text) = await DevicePairedTemplates.RenderAsync(renderer, GoldenModel(), SecurityFooter);

        GoldenFile.AssertMatches(html, "device-paired.approved.html");
        GoldenFile.AssertMatches(text, "device-paired.approved.txt");
    }

    internal static readonly EmailFooterModel SecurityFooter = CampaignTestData.Footer with
    {
        ReasonLine = DevicePairedCampaign.FooterReason,
        UnsubscribeUrl = null,
    };

    internal static DevicePairedModel GoldenModel() => new(
        "Ada Lovelace",
        "printlog-bridge on voron",
        Repaired: false,
        "October 30, 2026 at 3:04 PM (America/Chicago)",
        "Log prints, see printers, update printer slots, send heartbeats",
        "https://www.3dprintlog.test/settings?utm_source=email#connected-agents",
        "203.0.113.7");
}
