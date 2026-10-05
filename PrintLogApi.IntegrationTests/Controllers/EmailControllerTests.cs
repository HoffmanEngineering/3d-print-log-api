using System.Net;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Options;
using PrintLogApi.Email;
using PrintLogApi.Email.Tokens;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.IntegrationTests.Email;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Email;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

public class EmailControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public EmailControllerTests(CustomWebApplicationFactory factory) => _factory = factory;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private IEmailTokenService Tokens => _factory.Services.GetRequiredService<IEmailTokenService>();

    private async Task<User> UserAsync(string? email = null)
    {
        using var scope = _factory.Services.CreateScope();
        return await EmailTestData.CreateUserAsync(scope.ServiceProvider.GetRequiredService<PrintLogContext>(), email: email);
    }

    private async Task<EmailPreferenceSnapshot> PrefsAsync(long userId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEmailPreferenceService>().GetAsync(userId, Ct);
    }

    private static FormUrlEncodedContent OneClickBody(string value = "One-Click")
        => new([new KeyValuePair<string, string>("List-Unsubscribe", value)]);

    private Task<HttpResponseMessage> OneClickAsync(string token, HttpContent? body)
        => _factory.CreateClient().PostAsync($"/api/email/unsubscribe?t={token}", body, Ct);

    private static HttpRequestMessage WithToken(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Add(EmailTokenHeader.Name, token);
        }
        return request;
    }

    // ---------- RFC 8058 one-click (Review Focus 3) ----------

    [Fact]
    public async Task OneClick_GetDoesNotUnsubscribe()
    {
        var user = await UserAsync();
        var token = Tokens.CreateUnsubscribe(user.Id, EmailSettingTypes.MonthlyRecap);

        var response = await _factory.CreateClient().GetAsync($"/api/email/unsubscribe?t={token}", Ct);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.True((await PrefsAsync(user.Id)).MonthlyRecap);
    }

    [Fact]
    public async Task OneClick_EmptyBodyRejected()
    {
        var user = await UserAsync();

        var response = await OneClickAsync(Tokens.CreateUnsubscribe(user.Id, EmailSettingTypes.MonthlyRecap), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await PrefsAsync(user.Id)).MonthlyRecap);
    }

    [Fact]
    public async Task OneClick_FormBody_Unsubscribes()
    {
        var user = await UserAsync();

        var response = await OneClickAsync(Tokens.CreateUnsubscribe(user.Id, EmailSettingTypes.MonthlyRecap), OneClickBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
        var prefs = await PrefsAsync(user.Id);
        Assert.False(prefs.MonthlyRecap);
        Assert.True(prefs.PrinterSilent);
        Assert.True(prefs.All);
    }

    [Fact]
    public async Task OneClick_MultipartBody_Unsubscribes()
    {
        var user = await UserAsync();
        using var body = new MultipartFormDataContent { { new StringContent("One-Click"), "List-Unsubscribe" } };

        var response = await OneClickAsync(Tokens.CreateUnsubscribe(user.Id, EmailSettingTypes.PrinterSilent), body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await PrefsAsync(user.Id)).PrinterSilent);
    }

    [Fact]
    public async Task OneClick_WrongValueRejected()
    {
        var user = await UserAsync();

        var response = await OneClickAsync(Tokens.CreateUnsubscribe(user.Id, EmailSettingTypes.MonthlyRecap), OneClickBody("Yes"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await PrefsAsync(user.Id)).MonthlyRecap);
    }

    [Fact]
    public async Task OneClick_AllCategory_TurnsEverythingOff()
    {
        var user = await UserAsync();

        await OneClickAsync(Tokens.CreateUnsubscribe(user.Id, EmailSettingTypes.All), OneClickBody());

        Assert.False((await PrefsAsync(user.Id)).All);
    }

    [Fact]
    public async Task OneClick_IsIdempotent()
    {
        var user = await UserAsync();
        var token = Tokens.CreateUnsubscribe(user.Id, EmailSettingTypes.MonthlyRecap);

        await OneClickAsync(token, OneClickBody());
        var second = await OneClickAsync(token, OneClickBody());

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task OneClick_InvalidToken400()
        => Assert.Equal(HttpStatusCode.BadRequest, (await OneClickAsync("not-a-token", OneClickBody())).StatusCode);

    [Fact]
    public async Task OneClick_ManageTokenRejected400()
    {
        var user = await UserAsync();

        var response = await OneClickAsync(Tokens.CreateManage(user.Id), OneClickBody());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // A validly signed token naming a setting that is not an email category must not become a
    // way to write arbitrary user settings.
    [Fact]
    public async Task OneClick_NonEmailCategoryRejected()
    {
        var user = await UserAsync();

        var response = await OneClickAsync(Tokens.CreateUnsubscribe(user.Id, EmailSettingTypes.NoticeSeenAt), OneClickBody());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The account was deleted after the email went out. Nothing to unsubscribe; still a success.
    [Fact]
    public async Task OneClick_DeletedUser_Succeeds()
    {
        var response = await OneClickAsync(Tokens.CreateUnsubscribe(long.MaxValue - 7, EmailSettingTypes.MonthlyRecap), OneClickBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- footer-link page ----------

    [Fact]
    public async Task Confirm_UnsubscribesWithHeaderToken()
    {
        var user = await UserAsync();
        using var request = WithToken(HttpMethod.Post, "/api/email/unsubscribe/confirm", Tokens.CreateUnsubscribe(user.Id, EmailSettingTypes.MonthlyRecap));

        var response = await _factory.CreateClient().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new UnsubscribeConfirmedDto(EmailSettingTypes.MonthlyRecap), await response.Content.ReadFromJsonAsync<UnsubscribeConfirmedDto>(Ct));
        Assert.False((await PrefsAsync(user.Id)).MonthlyRecap);
    }

    [Fact]
    public async Task Confirm_MissingHeader400()
    {
        using var request = WithToken(HttpMethod.Post, "/api/email/unsubscribe/confirm", null);
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.CreateClient().SendAsync(request, Ct)).StatusCode);
    }

    // ---------- manage page ----------

    [Fact]
    public async Task ReadPreferences_ReturnsMaskedEmailAndFlags()
    {
        var user = await UserAsync(email: "christopher@example.com");
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IEmailPreferenceService>().SetAsync(user.Id, EmailSettingTypes.PrinterSilent, false, Ct);
        }
        using var request = WithToken(HttpMethod.Get, "/api/email/preferences", Tokens.CreateManage(user.Id));

        var response = await _factory.CreateClient().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new EmailPreferencesDto("c•••@example.com", All: true, Onboarding: true, MonthlyRecap: true, PrinterSilent: false),
            await response.Content.ReadFromJsonAsync<EmailPreferencesDto>(Ct));
    }

    [Fact]
    public async Task ReadPreferences_UnsubscribeTokenRejected()
    {
        var user = await UserAsync();
        using var request = WithToken(HttpMethod.Get, "/api/email/preferences", Tokens.CreateUnsubscribe(user.Id, EmailSettingTypes.All));

        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.CreateClient().SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task ReadPreferences_ExpiredManageTokenRejected()
    {
        var user = await UserAsync();
        var oldClock = new SettableTimeProvider(DateTimeOffset.UtcNow.AddDays(-61));
        var oldToken = new EmailTokenService(_factory.Services.GetRequiredService<IOptions<EmailOptions>>(), oldClock).CreateManage(user.Id);
        using var request = WithToken(HttpMethod.Get, "/api/email/preferences", oldToken);

        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.CreateClient().SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task UpdatePreferences_PersistsAll()
    {
        var user = await UserAsync();
        using var request = WithToken(HttpMethod.Put, "/api/email/preferences", Tokens.CreateManage(user.Id));
        request.Content = JsonContent.Create(new UpdateEmailPreferencesRequest(All: true, Onboarding: false, MonthlyRecap: true, PrinterSilent: false));

        var response = await _factory.CreateClient().SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var prefs = await PrefsAsync(user.Id);
        Assert.Equal((true, false, true, false), (prefs.All, prefs.Onboarding, prefs.MonthlyRecap, prefs.PrinterSilent));
    }

    [Theory]
    [InlineData("christopher@example.com", "c•••@example.com")]
    [InlineData("a@b.co", "a•••@b.co")]
    [InlineData("noatsign", "•••")]
    [InlineData("@example.com", "•••")]
    public void Mask(string email, string expected) => Assert.Equal(expected, EmailMasking.Mask(email));

    // ---------- telemetry ----------

    /// <summary>Every string a telemetry item would carry to Application Insights.</summary>
    private static IEnumerable<string> Strings(Microsoft.ApplicationInsights.Channel.ITelemetry item)
    {
        if (item is ISupportProperties withProperties)
        {
            foreach (var pair in withProperties.Properties)
            {
                yield return pair.Key + "=" + pair.Value;
            }
        }

        switch (item)
        {
            case RequestTelemetry r:
                yield return r.Url?.ToString() ?? "";
                yield return r.Name ?? "";
                break;
            case DependencyTelemetry d:
                yield return d.Data ?? "";
                yield return d.Name ?? "";
                break;
            case TraceTelemetry t:
                yield return t.Message ?? "";
                break;
            case ExceptionTelemetry e:
                yield return e.Exception?.ToString() ?? e.Message ?? "";
                break;
        }
    }

    [Fact]
    public async Task Telemetry_NeverCarriesTheToken()
    {
        var user = await UserAsync();
        var unsubscribe = Tokens.CreateUnsubscribe(user.Id, EmailSettingTypes.MonthlyRecap);
        var manage = Tokens.CreateManage(user.Id);
        _factory.Telemetry.Clear();

        await OneClickAsync(unsubscribe, OneClickBody());
        using var request = WithToken(HttpMethod.Get, "/api/email/preferences", manage);
        await _factory.CreateClient().SendAsync(request, Ct);

        // Events, traces and exceptions from the two calls. Request items are covered by the
        // test below: the SDK's request tracking hooks a process-wide listener, so whether a
        // given test host's channel receives them depends on what else is running.
        foreach (var text in _factory.Telemetry.Items.SelectMany(Strings))
        {
            Assert.DoesNotContain(unsubscribe, text);
            Assert.DoesNotContain(manage, text);
        }
    }

    // Proves the redaction initializer is wired into the host's telemetry pipeline, not just
    // that the class works in isolation.
    [Fact]
    public void Telemetry_HostRedactsOneClickRequestUrl()
    {
        var configuration = _factory.Services.GetRequiredService<Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration>();
        var request = new RequestTelemetry
        {
            Url = new Uri("https://api.3dprintlog.test/api/email/unsubscribe?t=SECRET"),
            Name = "POST Email/OneClick",
        };

        foreach (var initializer in configuration.TelemetryInitializers)
        {
            initializer.Initialize(request);
        }

        Assert.DoesNotContain("SECRET", request.Url.ToString());
    }
}
