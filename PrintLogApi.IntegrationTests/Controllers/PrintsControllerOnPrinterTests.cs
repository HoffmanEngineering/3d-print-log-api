using System.Net;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// <c>GET /api/Prints/on-printer</c> (printlog-bridge#8): a connector backfilling its printer's
/// history looks up the prints already logged on that printer in a time window, so it does not
/// import a second copy of a print the user logged with a slicer plugin or by hand.
/// </summary>
public class PrintsControllerOnPrinterTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly DateTimeOffset Day = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly HttpClient _httpClient;

    public PrintsControllerOnPrinterTests(CustomWebApplicationFactory factory)
    {
        _httpClient = factory.CreateClient();
    }

    [Fact]
    public async Task OnPrinter_ListsYourPrintsOnThatPrinterStartedInTheWindow_OldestFirst()
    {
        var printerId = await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId);
        var otherPrinterId = await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId);
        var before = await CreatePrintAsync(printerId, "before", Day.AddDays(-1));
        var first = await CreatePrintAsync(printerId, "first", Day.AddHours(2), fileName: "first.gcode",
            externalSource: "octoprint", externalId: "job-1");
        var second = await CreatePrintAsync(printerId, "second", Day.AddHours(5));
        await CreatePrintAsync(otherPrinterId, "elsewhere", Day.AddHours(3));
        var after = await CreatePrintAsync(printerId, "after", Day.AddDays(1));

        var prints = await ListAsync(printerId, Day, Day.AddDays(1));

        Assert.Equal([first, second], prints.Select(p => p.Id));
        var row = prints[0];
        Assert.Equal("first", row.Title);
        Assert.Equal("first.gcode", row.FileName);
        Assert.Equal(PrintStatus.Success, row.Status);
        Assert.Equal(Day.AddHours(2), row.StartDate);
        Assert.Equal("octoprint", row.ExternalSource);
        Assert.Equal("job-1", row.ExternalId);
        Assert.NotEqual(default, row.CreatedDate);
        Assert.DoesNotContain(prints, p => p.Id == before || p.Id == after);
    }

    [Fact]
    public async Task OnPrinter_APrintWithNoStartDate_IsPlacedByWhenItWasCreated()
    {
        var printerId = await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId);
        var undated = await CreatePrintAsync(printerId, "hand entry", startDate: null);

        var now = DateTimeOffset.UtcNow;
        var inWindow = await ListAsync(printerId, now.AddHours(-1), now.AddHours(1));
        var outOfWindow = await ListAsync(printerId, now.AddDays(-3), now.AddDays(-2));

        Assert.Equal([undated], inWindow.Select(p => p.Id));
        Assert.Null(inWindow[0].StartDate);
        Assert.Empty(outOfWindow);
    }

    [Fact]
    public async Task OnPrinter_AnotherUsersPrinter_IsEmpty()
    {
        var theirs = await CreatePrinterAsync(IntegrationTestSeeder.SecondaryUserOAuthId);
        await CreatePrintAsync(theirs, "theirs", Day.AddHours(1), IntegrationTestSeeder.SecondaryUserOAuthId);

        var prints = await ListAsync(theirs, Day, Day.AddDays(1));

        Assert.Empty(prints);
    }

    [Fact]
    public async Task OnPrinter_PagesWithLimitAndAfterId()
    {
        var printerId = await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId);
        var ids = new List<long>();
        for (var n = 0; n < 5; n++)
        {
            ids.Add(await CreatePrintAsync(printerId, $"print {n}", Day.AddHours(n)));
        }

        var firstPage = await ListAsync(printerId, Day, Day.AddDays(1), limit: 2);
        var secondPage = await ListAsync(printerId, Day, Day.AddDays(1), limit: 2, afterId: firstPage[^1].Id);
        var lastPage = await ListAsync(printerId, Day, Day.AddDays(1), limit: 2, afterId: secondPage[^1].Id);

        Assert.Equal(ids, firstPage.Concat(secondPage).Concat(lastPage).Select(p => p.Id));
        Assert.Single(lastPage);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=501")]
    public async Task OnPrinter_LimitOutOfRange_Returns400(string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/Prints/on-printer?printerId={IntegrationTestSeeder.TestPrinterId}&from=2026-08-01T00:00:00Z&to=2026-08-02T00:00:00Z&{query}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<List<PrintOnPrinterDto>> ListAsync(
        long printerId, DateTimeOffset from, DateTimeOffset to, int? limit = null, long? afterId = null)
    {
        var url = $"/api/Prints/on-printer?printerId={printerId}"
            + $"&from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}"
            + (limit is { } l ? $"&limit={l}" : "")
            + (afterId is { } a ? $"&afterId={a}" : "");
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<List<PrintOnPrinterDto>>(TestContext.Current.CancellationToken))!;
    }

    private async Task<long> CreatePrintAsync(
        long printerId, string title, DateTimeOffset? startDate,
        string userOAuthId = IntegrationTestSeeder.TestUserOAuthId,
        string? fileName = null, string? externalSource = null, string? externalId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        request.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = title,
            PrinterId = printerId,
            Status = PrintStatus.Success,
            ViewStatus = PrintViewStatus.Private,
            StartDate = startDate,
            FileName = fileName,
            ExternalSource = externalSource,
            ExternalId = externalId,
        });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!.Id;
    }

    private async Task<long> CreatePrinterAsync(string userOAuthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        request.Content = JsonContent.Create(new AddPrinterDTO { Name = "On-printer test", Make = "Make", Model = "Model", IsActive = true });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!.Id!.Value;
    }
}
