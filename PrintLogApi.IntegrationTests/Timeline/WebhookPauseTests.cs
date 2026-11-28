using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Connection;
using PrintLogApi.Models.DTOs.Moonraker;
using PrintLogApi.Models.DTOs.Octoprint;
using PrintLogApi.Models.DTOs.Printer;
using Xunit;

namespace PrintLogApi.IntegrationTests.Timeline;

/// <summary>Pauses from the Moonraker notifier and the OctoPrint Webhooks plugin (api#343).</summary>
public class WebhookPauseTests : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public WebhookPauseTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task MoonrakerPausedAndResumed_RecordAClosedPauseOnTheNotifiersPrint()
    {
        var printerId = await CreatePrinterAsync();
        var file = $"pause-{Guid.NewGuid():N}.gcode";
        await NotifyAsync("started", printerId, file);

        await NotifyAsync("paused", printerId, file);
        var paused = await PrintByFileAsync(file);
        Assert.NotNull(paused.PausedSince);

        await NotifyAsync("resumed", printerId, file);
        var resumed = await PrintByFileAsync(file);
        Assert.Null(resumed.PausedSince);
        Assert.NotNull(resumed.PausedSeconds);
        var pause = Assert.Single(await RowsAsync(resumed.Id), r => r.Type == PrintTimelineEventType.Paused);
        Assert.NotNull(pause.EndedAt);
        Assert.Equal(PrintSource.Moonraker, pause.Source);
    }

    [Fact]
    public async Task MoonrakerPaused_WithALiveBridgeAndNoNotifierPrint_IsDroppedAndCounted()
    {
        var printerId = await CreatePrinterAsync();
        var instanceId = await RegisterBridgeAsync(printerId, "moonraker");

        await NotifyAsync("paused", printerId, $"pause-{Guid.NewGuid():N}.gcode");

        Assert.Equal(1, await DroppedCountAsync(instanceId));
    }

    [Fact]
    public async Task OctoPrintPausedAndResumed_RecordAClosedPauseOnTheWebhooksPrint()
    {
        var printerId = await CreatePrinterAsync();
        var file = $"pause-{Guid.NewGuid():N}.gcode";
        await OctoPrintAsync("Print Started", printerId, file);

        await OctoPrintAsync("Print Paused", printerId, file);
        Assert.NotNull((await PrintByFileAsync(file)).PausedSince);

        await OctoPrintAsync("Print Resumed", printerId, file);
        var resumed = await PrintByFileAsync(file);
        Assert.Null(resumed.PausedSince);
        var pause = Assert.Single(await RowsAsync(resumed.Id), r => r.Type == PrintTimelineEventType.Paused);
        Assert.NotNull(pause.EndedAt);
        Assert.Equal(PrintSource.OctoPrint, pause.Source);
    }

    [Fact]
    public async Task OctoPrintPaused_WithALiveBridgeAndNoWebhookPrint_IsDroppedAndCounted()
    {
        var printerId = await CreatePrinterAsync();
        var instanceId = await RegisterBridgeAsync(printerId, "octoprint");

        await OctoPrintAsync("Print Paused", printerId, $"pause-{Guid.NewGuid():N}.gcode");

        Assert.Equal(1, await DroppedCountAsync(instanceId));
    }

    private async Task NotifyAsync(string eventName, long printerId, string file)
    {
        var message = new PrintEventMessageDto { EventName = eventName, Filename = file, PrinterId = printerId };
        var outer = JsonSerializer.Serialize(new PrintEventDto { Message = JsonSerializer.Serialize(message) },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Moonraker/notifier");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = new StringContent(outer, Encoding.UTF8, "application/json");
        var response = await _client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task OctoPrintAsync(string topic, long printerId, string file)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(topic), "Topic" },
            { new StringContent(printerId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "DeviceIdentifier" },
            { new StringContent(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)), "CurrentTime" },
            { new StringContent(JsonSerializer.Serialize(new OctoprintWebhookExtraDto { Name = file, Path = file })), "Extra" },
            { new StringContent(JsonSerializer.Serialize(new OctoprintWebhookJobDto { File = new OctoprintWebhookJobFileDto { Name = file } })), "Job" },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Octoprint");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = content;
        var response = await _client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<long> CreatePrinterAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new AddPrinterDTO { Name = $"Pause printer {Guid.NewGuid():N}", Make = "Make", Model = "Model", IsActive = true });
        var response = await _client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(Ct))!.Id!.Value;
    }

    private async Task<string> RegisterBridgeAsync(long printerId, string kind)
    {
        var instanceId = Guid.NewGuid().ToString();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Connections/{instanceId}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new PutConnectionDto { Kind = kind, DisplayName = "Pause bridge", AgentVersion = "0.1.0", PrinterId = printerId });
        var response = await _client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}");
        return instanceId;
    }

    private async Task<int> DroppedCountAsync(string instanceId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>().Connections
            .Where(c => c.InstanceId == instanceId).Select(c => c.DroppedNotifierEventCount).SingleAsync(Ct);
    }

    private async Task<Print> PrintByFileAsync(string file)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>().Prints
            .AsNoTracking().SingleAsync(p => p.FileName == file, Ct);
    }

    private async Task<List<PrintTimelineEvent>> RowsAsync(long printId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>().PrintTimelineEvents
            .AsNoTracking().Where(e => e.PrintId == printId).ToListAsync(Ct);
    }
}
