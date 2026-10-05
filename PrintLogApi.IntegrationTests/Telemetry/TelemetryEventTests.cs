using System.Net;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Project;
using PrintLogApi.Models.DTOs.UserSetting;
using PrintLogApi.Telemetry;
using PrintLogApi.Users;
using Xunit;

namespace PrintLogApi.IntegrationTests.Telemetry;

/// <summary>
/// The events the Usage workbook charts, asserted end to end through the recording channel:
/// each one is emitted on success with the properties the workbook queries, stamped with the
/// caller's user id and <c>authMethod</c>, and not emitted when the request fails.
/// </summary>
/// <remarks>
/// Only <em>events</em> are asserted here. Request telemetry is produced by the SDK's hosting
/// listener, which subscribes to the process-wide diagnostic source — so with many test hosts
/// alive in parallel, the request one host serves is routinely tracked by another host's
/// module into that host's channel. The processor and the stamping are unit-tested on their
/// own; the event assertions below prove both run inside the real pipeline.
/// </remarks>
public class TelemetryEventTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly CustomWebApplicationFactory _factory;

    public TelemetryEventTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static HttpRequestMessage AuthenticatedRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return request;
    }

    private async Task<ProjectDetailDto> CreateProject(string name)
    {
        var req = AuthenticatedRequest(HttpMethod.Post, "/api/Projects");
        req.Content = JsonContent.Create(new AddProjectDto
        {
            Name = name,
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = Project.ProjectViewStatus.Private
        });
        var resp = await _client.SendAsync(req, TestContext.Current.CancellationToken);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ProjectDetailDto>(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task Project_lifecycle_emits_one_event_per_write_stamped_with_the_caller()
    {
        _factory.Telemetry.Clear();

        var project = await CreateProject("telemetry-project");

        var put = AuthenticatedRequest(HttpMethod.Put, $"/api/Projects/{project.Id}");
        put.Content = JsonContent.Create(new PutProjectDto
        {
            Id = project.Id,
            Name = "telemetry-project-renamed",
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = Project.ProjectViewStatus.Private
        });
        (await _client.SendAsync(put, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var delete = AuthenticatedRequest(HttpMethod.Delete, $"/api/Projects/{project.Id}?deletePrints=true");
        (await _client.SendAsync(delete, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var added = Assert.Single(_factory.Telemetry.Events("ProjectAdded"));
        Assert.Single(_factory.Telemetry.Events("ProjectEdit"));
        var deleted = Assert.Single(_factory.Telemetry.Events("ProjectDelete"));

        Assert.Equal("true", deleted.Properties["deletePrints"]);
        Assert.Equal(IntegrationTestSeeder.TestUserId.ToString(), added.Context.User.AuthenticatedUserId);
        Assert.Equal(TelemetryEnrichmentInitializer.Jwt, added.Properties[TelemetryEnrichmentInitializer.AuthMethodProperty]);
    }

    [Fact]
    public async Task Failed_project_write_emits_no_event()
    {
        _factory.Telemetry.Clear();

        var put = AuthenticatedRequest(HttpMethod.Put, $"/api/Projects/{Guid.NewGuid()}");
        put.Content = JsonContent.Create(new PutProjectDto { Id = Guid.NewGuid(), Name = "x" });
        var resp = await _client.SendAsync(put, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(_factory.Telemetry.Events("ProjectEdit"));
    }

    [Fact]
    public async Task User_settings_create_and_update_emit_the_setting_type()
    {
        _factory.Telemetry.Clear();

        // Type 3 (Prints_LastSelectedAllowComments) is a seeded setting type with no row for
        // the test user, so the create is a clean insert.
        var post = AuthenticatedRequest(HttpMethod.Post, "/api/Users/me/user-settings");
        post.Content = JsonContent.Create(new AddUserSettingDto { UserSettingTypeId = 3, Value = "true" });
        var created = await _client.SendAsync(post, TestContext.Current.CancellationToken);
        created.EnsureSuccessStatusCode();
        var setting = (await created.Content.ReadFromJsonAsync<UserSettingDto>(TestContext.Current.CancellationToken))!;

        var put = AuthenticatedRequest(HttpMethod.Put, "/api/Users/me/user-settings");
        put.Content = JsonContent.Create(new UpdateUserSettingDto { Id = setting.Id, Value = "false" });
        (await _client.SendAsync(put, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var events = _factory.Telemetry.Events("UserSettingsChanged").ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("3", e.Properties["settingTypeId"]));
    }

    [Fact]
    public async Task Creating_a_user_emits_UserSignedUp_once()
    {
        _factory.Telemetry.Clear();

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();

        var created = await users.CreateUserFromAuthId("auth0|telemetry-signup");

        Assert.NotEqual(0, created.Id);
        Assert.Single(_factory.Telemetry.Events("UserSignedUp"));

        // Tidy so the seeded user counts other tests rely on are unchanged.
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        await context.Users.Where(u => u.Id == created.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// Startup switches telemetry off in the test environments; this host does not switch it
/// back on, so it proves the Startup behaviour rather than the test factory's override.
/// </summary>
public sealed class TelemetryDisabledFactory : CustomWebApplicationFactory
{
    protected override bool CaptureTelemetry => false;
}

public class TelemetryDisabledInTestEnvironmentTests : IClassFixture<TelemetryDisabledFactory>
{
    private readonly TelemetryDisabledFactory _factory;

    public TelemetryDisabledInTestEnvironmentTests(TelemetryDisabledFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void IntegrationTesting_environment_disables_telemetry()
    {
        var configuration = _factory.Services.GetRequiredService<TelemetryConfiguration>();

        Assert.True(configuration.DisableTelemetry);
    }

    [Fact]
    public void Nothing_reaches_the_channel_when_disabled()
    {
        _factory.Telemetry.Clear();
        var client = _factory.Services.GetRequiredService<TelemetryClient>();

        client.TrackEvent("ShouldNotBeSent");
        client.Flush();

        Assert.Empty(_factory.Telemetry.Items);
    }
}
