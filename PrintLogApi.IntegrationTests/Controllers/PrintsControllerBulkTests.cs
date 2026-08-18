using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Project;
using PrintLogApi.Services;
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

    private static async Task AssertProblemDetailAsync(HttpResponseMessage response, string expectedFragment)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.False(string.IsNullOrWhiteSpace(problem!.Detail), "Phase-one rejections must carry a readable detail.");
        Assert.Contains(expectedFragment, problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BulkUpdate_EmptyPrintIds_ReturnsProblemDetails()
    {
        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = Array.Empty<long>(),
            status = (int)PrintStatus.Success
        });

        await AssertProblemDetailAsync(response, "at least one id");
    }

    [Fact]
    public async Task BulkUpdate_TooManyPrintIds_ReturnsProblemDetails()
    {
        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = Enumerable.Range(1, 201).Select(i => (long)i).ToArray(),
            status = (int)PrintStatus.Success
        });

        await AssertProblemDetailAsync(response, "at most 200");
    }

    [Fact]
    public async Task BulkUpdate_NoFieldsSet_ReturnsProblemDetails()
    {
        var print = await CreatePrintAsync("Bulk Empty Patch");

        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { print.Id }
        });

        await AssertProblemDetailAsync(response, "at least one field");
    }

    [Fact]
    public async Task BulkUpdate_DuplicatePrintIds_ReturnsProblemDetails()
    {
        var print = await CreatePrintAsync("Bulk Duplicate Ids");

        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { print.Id, print.Id },
            status = (int)PrintStatus.Success
        });

        await AssertProblemDetailAsync(response, "must not contain duplicates");
    }

    [Fact]
    public async Task BulkUpdate_ProjectSetAndCleared_ReturnsProblemDetails()
    {
        var print = await CreatePrintAsync("Bulk Conflicting Project");
        var project = await CreateProjectAsync("Bulk Conflict Project");

        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { print.Id },
            projectId = project.Id,
            clear = new[] { "projectId" }
        });

        await AssertProblemDetailAsync(response, "both set and cleared");
    }

    [Fact]
    public async Task BulkUpdate_NonExistentProject_ReturnsProblemDetailsAndWritesNothing()
    {
        var print = await CreatePrintAsync("Bulk Missing Project");

        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { print.Id },
            projectId = Guid.NewGuid()
        });

        await AssertProblemDetailAsync(response, "Project not found");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var stored = await db.Prints.AsNoTracking().FirstAsync(p => p.Id == print.Id, TestContext.Current.CancellationToken);
        Assert.Null(stored.ProjectId);
    }

    [Fact]
    public async Task BulkUpdate_ProjectOwnedBySomeoneElse_ReturnsProblemDetails()
    {
        // A real project that exists but belongs to another user. Without this case, a
        // service that checks existence and forgets ownership still passes.
        const string ownerOAuthId = "auth0|test-bulk-foreign-project-owner";
        var ownerId = await CreateOtherUserAsync(ownerOAuthId);
        var print = await CreatePrintAsync("Bulk Foreign Project");

        Guid foreignProjectId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var now = DateTime.UtcNow;
            var project = new PrintLogApi.Models.Project
            {
                Id = Guid.NewGuid(),
                Name = "Not Your Project",
                Status = Project.ProjectStatus.InProgress,
                ViewStatus = Project.ProjectViewStatus.Private,
                CreatedById = ownerId,
                CreatedDate = now,
                UpdatedById = ownerId,
                UpdatedDate = now
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            foreignProjectId = project.Id;
        }

        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { print.Id },
            projectId = foreignProjectId
        });

        await AssertProblemDetailAsync(response, "Project not found");

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var stored = await verifyDb.Prints.AsNoTracking()
            .FirstAsync(p => p.Id == print.Id, TestContext.Current.CancellationToken);
        Assert.Null(stored.ProjectId);
    }

    [Fact]
    public async Task BulkUpdate_NonExistentPrinter_ReturnsProblemDetails()
    {
        var print = await CreatePrintAsync("Bulk Missing Printer");

        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { print.Id },
            printerId = 999_999_999L
        });

        await AssertProblemDetailAsync(response, "Printer not found");
    }

    [Fact]
    public async Task BulkUpdate_PrinterOwnedBySomeoneElse_ReturnsProblemDetails()
    {
        const string ownerOAuthId = "auth0|test-bulk-foreign-printer-owner";
        var ownerId = await CreateOtherUserAsync(ownerOAuthId);
        var print = await CreatePrintAsync("Bulk Foreign Printer");

        long foreignPrinterId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var printer = new PrintLogApi.Models.Printer { Name = "Not Your Printer", UserId = ownerId };
            db.Printers.Add(printer);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            foreignPrinterId = printer.Id;
        }

        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { print.Id },
            printerId = foreignPrinterId
        });

        await AssertProblemDetailAsync(response, "Printer not found");
    }

    [Fact]
    public async Task BulkUpdate_UnknownClearField_ReturnsProblemDetails()
    {
        var print = await CreatePrintAsync("Bulk Bad Clear Field");

        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { print.Id },
            clear = new[] { "notes" }
        });

        await AssertProblemDetailAsync(response, "not a clearable field");
    }

    [Fact]
    public async Task BulkUpdate_AsPrinterOwner_InvalidatesTheCreatorsCacheToo()
    {
        // Arrange - a guest's print on the seeded user's printer.
        const string creatorOAuthId = "auth0|test-bulk-cache-creator";
        var creatorId = await CreateOtherUserAsync(creatorOAuthId);

        long printId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var print = NewPrint("Cache Invalidation Print", IntegrationTestSeeder.TestPrinterId, creatorId);
            db.Prints.Add(print);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            printId = print.Id;
        }

        string versionBefore;
        using (var scope = _factory.Services.CreateScope())
        {
            var cacheVersions = scope.ServiceProvider.GetRequiredService<ICacheVersionService>();
            versionBefore = cacheVersions.GetUserCacheVersion(creatorId);
        }

        // Act - the printer's owner, not the creator, performs the update.
        var response = await PostBulkAsync("/api/Prints/bulk-update", new
        {
            printIds = new[] { printId },
            status = (int)PrintStatus.Success
        });
        response.EnsureSuccessStatusCode();

        // Assert - the creator's cached summaries were invalidated, not just the caller's.
        using (var scope = _factory.Services.CreateScope())
        {
            var cacheVersions = scope.ServiceProvider.GetRequiredService<ICacheVersionService>();
            Assert.NotEqual(versionBefore, cacheVersions.GetUserCacheVersion(creatorId));
        }
    }

    [Fact]
    public async Task UpdatePrintStatus_AsPrinterOwner_InvalidatesTheCreatorsCacheToo()
    {
        // The same invariant on the single-item endpoint the bulk path replaces. Without
        // this, a printer owner's status change leaves the creator reading a stale list.
        const string creatorOAuthId = "auth0|test-single-status-cache-creator";
        var creatorId = await CreateOtherUserAsync(creatorOAuthId);

        long printId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var print = NewPrint("Single Status Cache Print", IntegrationTestSeeder.TestPrinterId, creatorId);
            db.Prints.Add(print);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            printId = print.Id;
        }

        string versionBefore;
        using (var scope = _factory.Services.CreateScope())
        {
            versionBefore = scope.ServiceProvider.GetRequiredService<ICacheVersionService>()
                .GetUserCacheVersion(creatorId);
        }

        var request = new HttpRequestMessage(
            HttpMethod.Put, $"/api/Prints/{printId}/status/{(int)PrintStatus.Success}");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            Assert.NotEqual(versionBefore, scope.ServiceProvider
                .GetRequiredService<ICacheVersionService>().GetUserCacheVersion(creatorId));
        }
    }

    [Fact]
    public async Task BulkDelete_RemovesThePrintsAndTheirRelatedRows()
    {
        var printOne = await CreatePrintAsync("Bulk Delete One");
        var printTwo = await CreatePrintAsync("Bulk Delete Two");

        // Seed the related rows first. Without this the assertions below are vacuous:
        // a freshly created print has no comments, images, attachments or notifications,
        // so "none exist afterwards" would already be true with the cascade removed.
        Guid fileId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var now = DateTime.UtcNow;
            var userId = IntegrationTestSeeder.TestUserId;

            var comment = new Comment
            {
                Body = "Nice print",
                CreatedById = userId,
                CreatedDate = now,
                UpdatedById = userId,
                UpdatedDate = now
            };
            db.Comments.Add(comment);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.PrintComments.Add(new PrintComment
            {
                PrintId = printOne.Id,
                CommentId = comment.Id,
                CreatedById = userId,
                CreatedDate = now,
                UpdatedById = userId,
                UpdatedDate = now
            });

            var file = new PrintLogApi.Models.File
            {
                Id = Guid.NewGuid(),
                Path = "attachments/test.gcode",
                Size = 42,
                CreatedById = userId,
                CreatedDate = now,
                UpdatedById = userId,
                UpdatedDate = now
            };
            db.Files.Add(file);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            fileId = file.Id;
            db.PrintAttachments.Add(new PrintAttachment
            {
                PrintId = printOne.Id,
                FileId = file.Id,
                OriginalFileName = "test.gcode",
                ContentType = "text/plain",
                CreatedById = userId,
                CreatedDate = now,
                UpdatedById = userId,
                UpdatedDate = now
            });

            db.PrintFilament.Add(new PrintFilament
            {
                PrintId = printTwo.Id,
                FilamentId = IntegrationTestSeeder.TestFilamentId1,
                AmountMg = 1200,
                Source = PrintFilament.SourceMeasurement.Weight,
                EstimatedSource = PrintFilament.SourceMeasurement.Weight
            });

            db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Type = NotificationType.PrintCompleted,
                Title = "Print finished",
                CreatedDate = now,
                PrintId = printTwo.Id
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await PostBulkAsync("/api/Prints/bulk-delete", new
        {
            printIds = new[] { printOne.Id, printTwo.Id }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BulkPrintResultDto>(TestContext.Current.CancellationToken))!;
        Assert.Equal(2, result.Succeeded.Count);
        Assert.Empty(result.Failed);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.False(await verifyDb.Prints.AnyAsync(p => p.Id == printOne.Id || p.Id == printTwo.Id,
            TestContext.Current.CancellationToken));
        Assert.False(await verifyDb.PrintFilament.AnyAsync(pf => pf.PrintId == printOne.Id || pf.PrintId == printTwo.Id,
            TestContext.Current.CancellationToken));
        Assert.False(await verifyDb.PrintImages.AnyAsync(pi => pi.PrintId == printOne.Id || pi.PrintId == printTwo.Id,
            TestContext.Current.CancellationToken));
        Assert.False(await verifyDb.PrintComments.AnyAsync(pc => pc.PrintId == printOne.Id || pc.PrintId == printTwo.Id,
            TestContext.Current.CancellationToken));
        Assert.False(await verifyDb.PrintAttachments.AnyAsync(a => a.PrintId == printOne.Id || a.PrintId == printTwo.Id,
            TestContext.Current.CancellationToken));
        Assert.False(await verifyDb.Files.AnyAsync(f => f.Id == fileId, TestContext.Current.CancellationToken));
        Assert.False(await verifyDb.Notifications.AnyAsync(n => n.PrintId == printOne.Id || n.PrintId == printTwo.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BulkDelete_AlreadyMissingId_IsReportedAsSucceeded()
    {
        // Delete is idempotent: the goal state is "this print is gone". A retry after a
        // lost response must not report the prints it already deleted as failures.
        var print = await CreatePrintAsync("Bulk Delete Idempotent");
        const long missingId = 999_999_998;

        var response = await PostBulkAsync("/api/Prints/bulk-delete", new
        {
            printIds = new[] { print.Id, missingId }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BulkPrintResultDto>(TestContext.Current.CancellationToken))!;
        Assert.Contains(missingId, result.Succeeded);
        Assert.Contains(print.Id, result.Succeeded);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public async Task BulkDelete_AsPrinterOwnerWhoDidNotCreateThePrint_IsForbidden()
    {
        // Delete is creator-only, unlike update. This asymmetry is deliberate.
        const string creatorOAuthId = "auth0|test-bulk-delete-guest-creator";
        var creatorId = await CreateOtherUserAsync(creatorOAuthId);

        long printId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var print = NewPrint("Guest Print Not Mine To Delete", IntegrationTestSeeder.TestPrinterId, creatorId);
            db.Prints.Add(print);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            printId = print.Id;
        }

        var response = await PostBulkAsync("/api/Prints/bulk-delete", new { printIds = new[] { printId } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BulkPrintResultDto>(TestContext.Current.CancellationToken))!;
        Assert.Empty(result.Succeeded);
        Assert.Equal("Forbidden", Assert.Single(result.Failed).Reason);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.True(await verifyDb.Prints.AnyAsync(p => p.Id == printId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BulkDelete_MixedBatch_DeletesTheOwnedPrintAndRefusesTheRest()
    {
        // A refusal in the middle of a batch must not stop the ids around it from being
        // deleted, and must not take the whole request down with it.
        const string creatorOAuthId = "auth0|test-bulk-delete-mixed-creator";
        var creatorId = await CreateOtherUserAsync(creatorOAuthId);

        var mine = await CreatePrintAsync("Bulk Delete Mixed Mine");
        const long missingId = 999_999_996;

        long theirs;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var print = NewPrint("Bulk Delete Mixed Theirs", IntegrationTestSeeder.TestPrinterId, creatorId);
            db.Prints.Add(print);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            theirs = print.Id;
        }

        var response = await PostBulkAsync("/api/Prints/bulk-delete", new
        {
            printIds = new[] { theirs, mine.Id, missingId }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<BulkPrintResultDto>(TestContext.Current.CancellationToken))!;
        Assert.Contains(mine.Id, result.Succeeded);
        Assert.Contains(missingId, result.Succeeded);
        Assert.Equal("Forbidden", Assert.Single(result.Failed).Reason);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.False(await verifyDb.Prints.AnyAsync(p => p.Id == mine.Id, TestContext.Current.CancellationToken));
        Assert.True(await verifyDb.Prints.AnyAsync(p => p.Id == theirs, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BulkDelete_EmptyPrintIds_ReturnsProblemDetails()
    {
        var response = await PostBulkAsync("/api/Prints/bulk-delete", new
        {
            printIds = Array.Empty<long>()
        });

        await AssertProblemDetailAsync(response, "at least one id");
    }

    [Fact]
    public async Task BulkDelete_TooManyPrintIds_ReturnsProblemDetails()
    {
        var response = await PostBulkAsync("/api/Prints/bulk-delete", new
        {
            printIds = Enumerable.Range(1, 201).Select(i => (long)i).ToArray()
        });

        await AssertProblemDetailAsync(response, "at most 200");
    }
}
