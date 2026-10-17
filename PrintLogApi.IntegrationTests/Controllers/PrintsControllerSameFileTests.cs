using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// The reprint counter (UI #10): <c>GET /api/Prints/{id}/same-file</c> counts and lists the
/// caller's prints of the same file, matched on <see cref="Print.FileHash"/>: the file's SHA-1
/// from OctoPrint, or a connector's own file identity, compared only within one user's prints.
/// </summary>
public class PrintsControllerSameFileTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient = factory.CreateClient();

    [Fact]
    public async Task SameFile_CountsYourPrintsOfTheHash_IncludingThisOne_AndListsTheOthers()
    {
        var hash = NewHash();
        var oldest = await CreatePrintAsync(hash, Day(1));
        var middle = await CreatePrintAsync(hash, Day(2));
        var newest = await CreatePrintAsync(hash, Day(3));
        var otherFile = await CreatePrintAsync(NewHash(), Day(4));
        var noHash = await CreatePrintAsync(null, Day(5));

        var result = await GetSameFileAsync(middle.Id);

        Assert.Equal(3, result.Count);
        Assert.Equal([newest.Id, oldest.Id], result.Prints!.Select(p => p.Id));
        Assert.DoesNotContain(result.Prints!, p => p.Id == otherFile.Id || p.Id == noHash.Id);
    }

    [Fact]
    public async Task SameFile_ListsWhatTheUiShows()
    {
        var hash = NewHash();
        var current = await CreatePrintAsync(hash, Day(1));
        var other = await CreatePrintAsync(hash, Day(2), PrintStatus.Failed);

        var listed = Assert.Single((await GetSameFileAsync(current.Id)).Prints!);

        Assert.Equal(other.Id, listed.Id);
        Assert.Equal(other.Title, listed.Title);
        Assert.Equal(PrintStatus.Failed, listed.Status);
        Assert.Equal(Day(2), listed.StartDate);
        Assert.Null(listed.DefaultPrintImageId);
    }

    [Fact]
    public async Task SameFile_PrintWithNoHash_IsZeroAndEmpty()
    {
        var print = await CreatePrintAsync(null, Day(1));
        await CreatePrintAsync(null, Day(2));

        var result = await GetSameFileAsync(print.Id);

        Assert.Equal(0, result.Count);
        Assert.Empty(result.Prints!);
    }

    [Fact]
    public async Task SameFile_OnlyPrint_IsOneAndEmpty()
    {
        var print = await CreatePrintAsync(NewHash(), Day(1));

        var result = await GetSameFileAsync(print.Id);

        Assert.Equal(1, result.Count);
        Assert.Empty(result.Prints!);
    }

    [Fact]
    public async Task SameFile_AnotherUsersPrintsOfTheSameHash_AreNotCounted()
    {
        var hash = NewHash();
        var mine = await CreatePrintAsync(hash, Day(1));
        var secondaryPrinter = await CreatePrinterAsync(IntegrationTestSeeder.SecondaryUserOAuthId);
        await CreatePrintAsync(hash, Day(2), printerId: secondaryPrinter, userOAuthId: IntegrationTestSeeder.SecondaryUserOAuthId);

        var result = await GetSameFileAsync(mine.Id);

        Assert.Equal(1, result.Count);
        Assert.Empty(result.Prints!);
    }

    [Theory]
    [InlineData(PrintViewStatus.Public)]
    [InlineData(PrintViewStatus.Private)]
    public async Task SameFile_AnotherUsersPrint_IsNotFound(PrintViewStatus viewStatus)
    {
        // Owner-only, public prints included: otherwise the counter is an oracle on someone
        // else's files, and a 403 would confirm the id exists.
        var hash = NewHash();
        var print = await CreatePrintAsync(hash, Day(1), viewStatus: viewStatus);
        await CreatePrintAsync(hash, Day(2), viewStatus: viewStatus);

        var response = await SendSameFileAsync(print.Id, IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SameFile_MissingPrint_IsNotFound()
    {
        var response = await SendSameFileAsync(long.MaxValue);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SameFile_Anonymous_IsUnauthorized()
    {
        var print = await CreatePrintAsync(NewHash(), Day(1), viewStatus: PrintViewStatus.Public);

        var response = await SendSameFileAsync(print.Id, userOAuthId: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SameFile_Limit_CapsTheListButNotTheCount()
    {
        var hash = NewHash();
        var current = await CreatePrintAsync(hash, Day(1));
        await CreatePrintAsync(hash, Day(2));
        await CreatePrintAsync(hash, Day(3));
        var newest = await CreatePrintAsync(hash, Day(4));

        var result = await GetSameFileAsync(current.Id, "limit=1");

        Assert.Equal(4, result.Count);
        Assert.Equal(newest.Id, Assert.Single(result.Prints!).Id);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=51")]
    public async Task SameFile_LimitOutOfRange_IsBadRequest(string query)
    {
        var print = await CreatePrintAsync(NewHash(), Day(1));

        var response = await SendSameFileAsync(print.Id, query: query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SameFile_PrintsWithNoStartDate_SortByCreation_AfterDatedOnes()
    {
        var hash = NewHash();
        var current = await CreatePrintAsync(hash, Day(1));
        var undatedFirst = await CreatePrintAsync(hash, null);
        var undatedSecond = await CreatePrintAsync(hash, null);
        var dated = await CreatePrintAsync(hash, Day(2));

        var result = await GetSameFileAsync(current.Id);

        Assert.Equal([dated.Id, undatedSecond.Id, undatedFirst.Id], result.Prints!.Select(p => p.Id));
    }

    [Fact]
    public async Task SameFile_HashlessPrintClaimedWithAHash_StartsCounting()
    {
        // The slicer uploader logs a print with no hash; the bridge claims it and sends its file
        // identity, which puts it in the same count as the bridge's earlier prints of that file.
        var hash = NewHash();
        var earlier = await CreatePrintAsync(hash, Day(1));
        var uploaded = await CreatePrintAsync(null, Day(2), PrintStatus.Printing);
        Assert.Equal(0, (await GetSameFileAsync(uploaded.Id)).Count);

        var claim = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{uploaded.Id}/claim");
        claim.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        claim.Content = JsonContent.Create(new ClaimPrintDto
        {
            ExternalSource = "moonraker",
            ExternalId = $"{Guid.NewGuid()}:1",
            FileHash = Convert.ToHexStringLower(hash),
        });
        var claimed = await _httpClient.SendAsync(claim, TestContext.Current.CancellationToken);
        Assert.True(claimed.IsSuccessStatusCode, await claimed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var result = await GetSameFileAsync(uploaded.Id);

        Assert.Equal(2, result.Count);
        Assert.Equal(earlier.Id, Assert.Single(result.Prints!).Id);
    }

    [Fact]
    public void SameFileIndex_IsBuiltOnline()
    {
        // Prints is the hottest table; an offline build holds a schema lock on it for the whole
        // build while the old app version is still serving (AGENTS.md, Deployment).
        using var context = new PrintLogContext(new DbContextOptionsBuilder<PrintLogContext>()
            .UseSqlServer("Server=unused;Database=unused;Trusted_Connection=True;")
            .Options);

        // The online flag is design-time configuration: the runtime model does not keep it.
        var index = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Print))!.GetIndexes()
            .Single(i => i.GetDatabaseName() == "IX_Prints_User_FileHash");

        Assert.True(index.IsCreatedOnline());
    }

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Day(int n) => Epoch.AddDays(n);

    private static byte[] NewHash() => Guid.NewGuid().ToByteArray().Concat(BitConverter.GetBytes(Random.Shared.Next())).ToArray();

    private async Task<PrintDetailDTO> CreatePrintAsync(
        byte[]? hash,
        DateTimeOffset? startDate,
        PrintStatus status = PrintStatus.Success,
        PrintViewStatus viewStatus = PrintViewStatus.Private,
        long? printerId = null,
        string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        // The hash goes in through REST, as a connector sends it: the SHA-1 OctoPrint reports, or
        // a connector's own 20-byte file identity (#215).
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        request.Content = JsonContent.Create(new AddPrintDTO
        {
            Title = $"same file {Guid.NewGuid():N}",
            PrinterId = printerId ?? IntegrationTestSeeder.TestPrinterId,
            Status = status,
            ViewStatus = viewStatus,
            StartDate = startDate,
            FileHash = hash is null ? null : Convert.ToHexStringLower(hash),
            FilamentUsage = [],
        });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
    }

    private async Task<long> CreatePrinterAsync(string userOAuthId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Printers");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        request.Content = JsonContent.Create(new AddPrinterDTO { Name = "Same file printer", Make = "Make", Model = "Model", IsActive = true });
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!.Id!.Value;
    }

    private async Task<HttpResponseMessage> SendSameFileAsync(
        long id, string? userOAuthId = IntegrationTestSeeder.TestUserOAuthId, string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/Prints/{id}/same-file?{query}");
        if (userOAuthId != null)
        {
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        }

        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<SameFilePrintsDto> GetSameFileAsync(long id, string query = "")
    {
        var response = await SendSameFileAsync(id, query: query);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<SameFilePrintsDto>(TestContext.Current.CancellationToken))!;
    }
}
