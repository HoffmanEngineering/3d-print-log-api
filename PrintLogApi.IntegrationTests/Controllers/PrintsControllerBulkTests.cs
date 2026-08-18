using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Project;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

public class PrintsControllerBulkTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;
    private readonly CustomWebApplicationFactory _factory;

    public PrintsControllerBulkTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    private async Task<PrintDetailDTO> CreatePrintAsync(string title)
    {
        var newPrint = new AddPrintDTO
        {
            Title = title,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Private,
            AllowComments = false
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(newPrint);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
    }

    private async Task<ProjectDetailDto> CreateProjectAsync(string name)
    {
        var dto = new AddProjectDto
        {
            Name = name,
            Status = Project.ProjectStatus.InProgress,
            ViewStatus = Project.ProjectViewStatus.Private
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Projects");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(dto);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProjectDetailDto>(TestContext.Current.CancellationToken))!;
    }

    private async Task<HttpResponseMessage> PostBulkAsync(string path, object body, string? oauthId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, oauthId ?? IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(body);
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BulkUpdate_AppliesEveryFieldAndLeavesOmittedFieldsAlone()
    {
        // Arrange
        var printOne = await CreatePrintAsync("Bulk Update One");
        var printTwo = await CreatePrintAsync("Bulk Update Two");
        var project = await CreateProjectAsync("Bulk Update Project");

        // Every field the contract supports except allowComments, which is deliberately
        // omitted so the test can prove an omitted field is left alone.
        var body = new
        {
            printIds = new[] { printOne.Id, printTwo.Id },
            status = (int)PrintStatus.Success,
            projectId = project.Id,
            viewStatus = (int)PrintViewStatus.Public,
            printerId = IntegrationTestSeeder.TestPrinterId2,
            allowFileDownloads = true
        };

        // Act
        var response = await PostBulkAsync("/api/Prints/bulk-update", body);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BulkPrintResultDto>(TestContext.Current.CancellationToken))!;
        Assert.Equal(new[] { printOne.Id, printTwo.Id }.OrderBy(id => id), result.Succeeded.OrderBy(id => id));
        Assert.Empty(result.Failed);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var stored = await db.Prints.AsNoTracking()
            .Where(p => p.Id == printOne.Id || p.Id == printTwo.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.All(stored, p =>
        {
            Assert.Equal(PrintStatus.Success, p.Status);
            Assert.Equal(project.Id, p.ProjectId);
            Assert.Equal(PrintViewStatus.Public, p.ViewStatus);
            Assert.Equal(IntegrationTestSeeder.TestPrinterId2, p.PrinterId);
            Assert.True(p.AllowFileDownloads);
            // Not in the request, so it must not have moved off its created value.
            Assert.False(p.AllowComments);
        });
        Assert.Contains(stored, p => p.Title == "Bulk Update One");
    }

    [Fact]
    public async Task BulkUpdate_ClearProjectId_RemovesTheAssignment()
    {
        var print = await CreatePrintAsync("Bulk Clear Project");
        var project = await CreateProjectAsync("Bulk Clear Project Target");

        await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { print.Id },
            projectId = project.Id
        });

        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { print.Id },
            clear = new[] { "projectId" }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var stored = await db.Prints.AsNoTracking()
            .FirstAsync(p => p.Id == print.Id, TestContext.Current.CancellationToken);
        Assert.Null(stored.ProjectId);
    }

    /// <summary>
    /// Creates a user who owns neither the seeded print nor the seeded printer.
    /// </summary>
    private async Task<long> CreateOtherUserAsync(string oauthId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var existing = await db.Users.FirstOrDefaultAsync(u => u.OAuthUserId == oauthId, TestContext.Current.CancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var user = new User { OAuthUserId = oauthId, ViewStatus = User.ProfileViewStatus.Public };
        db.Users.Add(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    /// <summary>
    /// Inserts a print directly, bypassing the API, so a test can own a print it could not
    /// create through an endpoint - one created by another user, say. The timestamp columns
    /// are set explicitly because <c>UpdatedById</c> is a non-nullable foreign key and the
    /// default 0 does not name a user.
    /// </summary>
    private static PrintLogApi.Models.Print NewPrint(string title, long printerId, long userId)
    {
        var now = DateTime.UtcNow;
        return new PrintLogApi.Models.Print
        {
            Title = title,
            PrinterId = printerId,
            Status = PrintStatus.Pending,
            ViewStatus = PrintViewStatus.Private,
            CreatedById = userId,
            CreatedDate = now,
            UpdatedById = userId,
            UpdatedDate = now
        };
    }

    [Fact]
    public async Task BulkUpdate_UnknownId_ReportsNotFoundAndStillUpdatesTheRest()
    {
        var print = await CreatePrintAsync("Bulk Mixed Known");
        const long missingId = 999_999_999;

        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { print.Id, missingId },
            status = (int)PrintStatus.Failed
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BulkPrintResultDto>(TestContext.Current.CancellationToken))!;

        Assert.Equal([print.Id], result.Succeeded);
        var failure = Assert.Single(result.Failed);
        Assert.Equal(missingId, failure.Id);
        Assert.Equal("NotFound", failure.Reason);
    }

    [Fact]
    public async Task BulkUpdate_PrintOwnedBySomeoneElse_ReportsForbidden()
    {
        const string otherUserOAuthId = "auth0|test-bulk-update-outsider";
        await CreateOtherUserAsync(otherUserOAuthId);
        var print = await CreatePrintAsync("Bulk Forbidden");

        // The outsider owns neither the print nor the printer it ran on.
        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { print.Id },
            status = (int)PrintStatus.Success
        }, otherUserOAuthId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BulkPrintResultDto>(TestContext.Current.CancellationToken))!;

        Assert.Empty(result.Succeeded);
        var failure = Assert.Single(result.Failed);
        Assert.Equal(print.Id, failure.Id);
        Assert.Equal("Forbidden", failure.Reason);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var stored = await db.Prints.AsNoTracking().FirstAsync(p => p.Id == print.Id, TestContext.Current.CancellationToken);
        Assert.Equal(PrintStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task BulkUpdate_MixedBatch_UpdatesTheAuthorizedIdsAndReportsTheRest()
    {
        // One request containing an authorized print, a forbidden one, and a missing one.
        // Testing these in separate requests would let an implementation that aborts the
        // whole batch on its first refusal pass.
        const string outsiderOAuthId = "auth0|test-bulk-update-mixed-outsider";
        var outsiderId = await CreateOtherUserAsync(outsiderOAuthId);

        var mine = await CreatePrintAsync("Bulk Mixed Mine");
        const long missingId = 999_999_997;

        long theirs;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var theirPrinter = new PrintLogApi.Models.Printer
            {
                Name = "Outsider Printer",
                UserId = outsiderId
            };
            db.Printers.Add(theirPrinter);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var print = NewPrint("Bulk Mixed Theirs", theirPrinter.Id, outsiderId);
            db.Prints.Add(print);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            theirs = print.Id;
        }

        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { mine.Id, theirs, missingId },
            status = (int)PrintStatus.Success
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BulkPrintResultDto>(TestContext.Current.CancellationToken))!;

        Assert.Equal([mine.Id], result.Succeeded);
        Assert.Equal("Forbidden", result.Failed.Single(f => f.Id == theirs).Reason);
        Assert.Equal("NotFound", result.Failed.Single(f => f.Id == missingId).Reason);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PrintLogContext>();
        // The authorized print was written even though the same request contained a refusal.
        Assert.Equal(PrintStatus.Success,
            (await verifyDb.Prints.AsNoTracking().FirstAsync(p => p.Id == mine.Id, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(PrintStatus.Pending,
            (await verifyDb.Prints.AsNoTracking().FirstAsync(p => p.Id == theirs, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task BulkUpdate_AsPrinterOwnerWhoDidNotCreateThePrint_Succeeds()
    {
        // Arrange - a second user creates a print on the seeded user's printer, so the
        // seeded user is the printer's owner but not the print's creator.
        const string creatorOAuthId = "auth0|test-bulk-update-guest-creator";
        var creatorId = await CreateOtherUserAsync(creatorOAuthId);

        long printId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var print = NewPrint("Guest Print On My Printer", IntegrationTestSeeder.TestPrinterId, creatorId);
            db.Prints.Add(print);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            printId = print.Id;
        }

        // Act - the printer's owner updates it.
        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { printId },
            status = (int)PrintStatus.Success
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BulkPrintResultDto>(TestContext.Current.CancellationToken))!;
        Assert.Equal([printId], result.Succeeded);
        Assert.Empty(result.Failed);
    }
}
