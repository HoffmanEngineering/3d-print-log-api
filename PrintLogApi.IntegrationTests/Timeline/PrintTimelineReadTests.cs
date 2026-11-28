using System.Text.Json;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Services;
using PrintLogApi.Services.Timeline;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Timeline;

/// <summary>Where the pause state and the timeline are read (api#343).</summary>
public class PrintTimelineReadTests : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PrintTimelineReadTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Detail_ShowsPausedSinceToEveryone_AndPausedSecondsOnlyToTheCreator()
    {
        var (id, start) = await CreatePublicPrintingAsync();
        await ApplyAsync(id, (TimelineInputType.Pause, start.AddMinutes(5), "p1"), (TimelineInputType.Resume, start.AddMinutes(8), "r1"),
            (TimelineInputType.Pause, start.AddMinutes(10), "p2"));

        var owner = await DetailAsync(id, IntegrationTestSeeder.TestUserOAuthId);
        var visitor = await DetailAsync(id, IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(start.AddMinutes(10), owner.GetProperty("pausedSince").GetDateTimeOffset());
        Assert.Equal(180, owner.GetProperty("pausedSeconds").GetInt32());
        Assert.Equal(start.AddMinutes(10), visitor.GetProperty("pausedSince").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, visitor.GetProperty("pausedSeconds").ValueKind);
    }

    [Fact]
    public async Task Summary_CarriesPausedSince()
    {
        var (id, start) = await CreatePublicPrintingAsync();
        await ApplyAsync(id, (TimelineInputType.Pause, start.AddMinutes(5), "p1"));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/summary?pageSize=50&sortRequest.sortColumn=StartDate");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _client.SendAsync(request, Ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        var item = json.RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt64() == id);
        Assert.Equal(start.AddMinutes(5), item.GetProperty("pausedSince").GetDateTimeOffset());
    }

    [Fact]
    public async Task McpDetail_HasTheTimelineAndPausedSeconds()
    {
        var (id, start) = await CreatePublicPrintingAsync();
        await ApplyAsync(id, (TimelineInputType.Pause, start.AddMinutes(5), "p1"), (TimelineInputType.Resume, start.AddMinutes(6), "r1"));

        using var scope = _factory.Services.CreateScope();
        var detail = await scope.ServiceProvider.GetRequiredService<IPrintService>()
            .GetOwnPrintDetailForMcp(IntegrationTestSeeder.TestUserId, id, Ct);

        Assert.Equal(60, detail!.PausedSeconds);
        Assert.Equal(["started", "paused"], detail.Timeline!.Select(e => e.Type));
        Assert.Equal(start.AddMinutes(6), detail.Timeline![1].EndedAt);
    }

    private async Task<JsonElement> DetailAsync(long id, string userOAuthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/{id}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        var response = await _client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private async Task ApplyAsync(long id, params (TimelineInputType Type, DateTimeOffset At, string Key)[] inputs)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPrintTimeline>().ApplyAsync(id,
            inputs.Select(i => new TimelineInput(i.Type, i.At, i.Key)).ToList(), PrintSource.ApiKey, Ct);
    }

    private async Task<(long Id, DateTimeOffset Start)> CreatePublicPrintingAsync()
    {
        // In the future of every other test's prints, so it sorts first in the summary.
        var start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds());
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = $"read {Guid.NewGuid():N}",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Printing,
            ViewStatus = PrintViewStatus.Public,
            StartDate = start,
            FilamentUsage = [],
        });
        var response = await _client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return ((await response.Content.ReadFromJsonAsync<PrintDetailDTO>(Ct))!.Id, start);
    }
}
