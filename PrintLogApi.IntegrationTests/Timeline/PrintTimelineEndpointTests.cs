using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Print;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Timeline;

/// <summary><c>POST</c> and <c>GET /api/Prints/{id}/timeline</c> (api#343).</summary>
public class PrintTimelineEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PrintTimelineEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Post_PauseThenResume_Answers204AndRecordsTheSpan()
    {
        var (id, start) = await CreatePrintingAsync();

        var response = await PostAsync(id, new
        {
            events = new object[]
            {
                new { type = "pause", occurredAt = start.AddMinutes(5), key = "k:pause:1" },
                new { type = "resume", occurredAt = start.AddMinutes(7), key = "k:resume:1" },
            },
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var print = await scope.ServiceProvider.GetRequiredService<PrintLogContext>().Prints
            .AsNoTracking().SingleAsync(p => p.Id == id, Ct);
        Assert.Null(print.PausedSince);
        Assert.Equal(120, print.PausedSeconds);
    }

    [Fact]
    public async Task Post_UnknownType_Is400()
    {
        var (id, start) = await CreatePrintingAsync();

        var response = await PostAsync(id, new { events = new[] { new { type = "runout", occurredAt = start, key = "k" } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_MoreThan20Events_Is400()
    {
        var (id, start) = await CreatePrintingAsync();
        var events = Enumerable.Range(0, 21).Select(i => new { type = "pause", occurredAt = start, key = $"k{i}" }).ToArray();

        var response = await PostAsync(id, new { events });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_KeyLongerThan200_Is400()
    {
        var (id, start) = await CreatePrintingAsync();

        var response = await PostAsync(id, new { events = new[] { new { type = "pause", occurredAt = start, key = new string('k', 201) } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_MissingTimeOrKey_Is400()
    {
        var (id, _) = await CreatePrintingAsync();

        var response = await PostAsync(id, new { events = new[] { new { type = "pause" } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_SomeoneElsesPrint_Is403()
    {
        var (id, start) = await CreatePrintingAsync();

        var response = await PostAsync(id, new { events = new[] { new { type = "pause", occurredAt = start, key = "k" } } },
            IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_NoSuchPrint_Is404()
    {
        var response = await PostAsync(long.MaxValue, new { events = new[] { new { type = "pause", occurredAt = DateTimeOffset.UtcNow, key = "k" } } });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Owner_ReadsTheTimelineInOrder()
    {
        var (id, start) = await CreatePrintingAsync();
        await PostAsync(id, new
        {
            events = new object[]
            {
                new { type = "pause", occurredAt = start.AddMinutes(5), key = "p" },
                new { type = "resume", occurredAt = start.AddMinutes(6), key = "r" },
            },
        });

        var response = await SendAsync(HttpMethod.Get, $"/api/Prints/{id}/timeline");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var events = json.RootElement.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(["started", "paused"], events.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal(start.AddMinutes(6), events[1].GetProperty("endedAt").GetDateTimeOffset());
        Assert.Equal("web", events[0].GetProperty("source").GetString());
    }

    [Fact]
    public async Task Get_VisitorOnAPublicPrint_Is403()
    {
        var (id, _) = await CreatePrintingAsync(PrintViewStatus.Public);

        var response = await SendAsync(HttpMethod.Get, $"/api/Prints/{id}/timeline", userOAuthId: IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<(long Id, DateTimeOffset Start)> CreatePrintingAsync(PrintViewStatus viewStatus = PrintViewStatus.Private)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());
        var response = await SendAsync(HttpMethod.Post, "/api/Prints", new AddPrintDTO
        {
            Title = $"timeline {Guid.NewGuid():N}",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Printing,
            ViewStatus = viewStatus,
            StartDate = start,
            FilamentUsage = [],
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return ((await response.Content.ReadFromJsonAsync<PrintDetailDTO>(Ct))!.Id, start);
    }

    private Task<HttpResponseMessage> PostAsync(long id, object body, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId) =>
        SendAsync(HttpMethod.Post, $"/api/Prints/{id}/timeline", body, userOAuthId);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, object? body = null, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await _client.SendAsync(request, Ct);
    }
}
