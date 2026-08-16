using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Moonraker;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

public class MoonrakerControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _httpClient;
    private readonly CustomWebApplicationFactory _factory;

    public MoonrakerControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    #region Helpers

    /// <summary>
    /// Creates the nested Moonraker webhook payload:
    /// outer: { "message": "<serialized PrintEventMessageDto>" }
    /// The inner message must use the JsonPropertyName attributes from PrintEventMessageDto
    /// </summary>
    private static StringContent CreateWebhookPayload(PrintEventMessageDto messageDto)
    {
        // The inner JSON must respect the [JsonPropertyName] attributes (snake_case)
        var innerJson = JsonSerializer.Serialize(messageDto);
        var outerDto = new PrintEventDto { Message = innerJson };
        var outerJson = JsonSerializer.Serialize(outerDto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new StringContent(outerJson, Encoding.UTF8, "application/json");
    }

    private HttpRequestMessage CreateAuthenticatedWebhookRequest(PrintEventMessageDto messageDto)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Moonraker/notifier");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = CreateWebhookPayload(messageDto);
        return request;
    }

    /// <summary>
    /// Finds a print by filename directly from the database.
    /// </summary>
    private Print? FindPrintByFileNameInDb(string expectedFileName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var fileName = System.IO.Path.GetFileName(expectedFileName);

        return db.Prints
            .Include(p => p.FilamentUsage)
            .Where(p => p.FileName == fileName && p.CreatedById == IntegrationTestSeeder.TestUserId)
            .OrderByDescending(p => p.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Seeds a user setting for the test user.
    /// </summary>
    private void SeedUserSetting(int userSettingTypeId, string value)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var existing = db.UserSettings
            .FirstOrDefault(u => u.UserId == IntegrationTestSeeder.TestUserId && u.UserSettingTypeId == userSettingTypeId);

        if (existing != null)
        {
            existing.Value = value;
        }
        else
        {
            db.UserSettings.Add(new UserSetting
            {
                UserId = IntegrationTestSeeder.TestUserId,
                UserSettingTypeId = userSettingTypeId,
                Value = value,
                CreatedById = IntegrationTestSeeder.TestUserId,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = DateTime.UtcNow,
                UpdatedDate = DateTime.UtcNow
            });
        }

        db.SaveChanges();
    }

    /// <summary>
    /// Removes user settings for the test user.
    /// </summary>
    private void ClearUserSettings()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var settings = db.UserSettings
            .Where(u => u.UserId == IntegrationTestSeeder.TestUserId)
            .ToList();
        db.UserSettings.RemoveRange(settings);
        db.SaveChanges();
    }

    /// <summary>
    /// Creates a second user and printer for cross-user access tests.
    /// Returns the printer ID belonging to the other user.
    /// </summary>
    private long SeedOtherUserWithPrinter()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

        var existingUser = db.Users.FirstOrDefault(u => u.OAuthUserId == "auth0|other-user");
        if (existingUser != null)
        {
            var existingPrinter = db.Printers.FirstOrDefault(p => p.UserId == existingUser.Id);
            return existingPrinter?.Id ?? 0;
        }

        var otherUser = new User
        {
            OAuthUserId = "auth0|other-user",
            ViewStatus = User.ProfileViewStatus.Public
        };
        db.Users.Add(otherUser);
        db.SaveChanges();

        var otherPrinter = new Printer
        {
            Name = "Other User's Printer",
            Model = "Other Model",
            Make = "Other Make",
            UserId = otherUser.Id,
            IsActive = true
        };
        db.Printers.Add(otherPrinter);
        db.SaveChanges();

        return otherPrinter.Id;
    }

    /// <summary>
    /// Generates a unique filename for each test to avoid conflicts.
    /// </summary>
    private static string UniqueFileName(string baseName)
    {
        return $"{baseName}_{Guid.NewGuid():N}.gcode";
    }

    #endregion

    #region Authentication Tests

    [Fact]
    public async Task Webhook_NotAuthenticated_ReturnsUnauthorized()
    {
        // Arrange - no auth header
        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = UniqueFileName("test_model"),
            PrinterId = IntegrationTestSeeder.TestPrinterId
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Moonraker/notifier");
        request.Content = CreateWebhookPayload(messageDto);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region Started Event Tests

    [Fact]
    public async Task Webhook_Started_ReturnsOk()
    {
        // Arrange
        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = UniqueFileName("started_ok_test"),
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var request = CreateAuthenticatedWebhookRequest(messageDto);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_Started_CreatesNewPrint()
    {
        // Arrange
        var filename = UniqueFileName("started_creates_print_test");
        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var request = CreateAuthenticatedWebhookRequest(messageDto);

        // Act
        var response = await _httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert - verify a print was created with the expected filename
        var print = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(print);
        Assert.Equal(PrintStatus.Printing, print.Status);
        Assert.Equal(System.IO.Path.GetFileName(filename), print.FileName);
        Assert.Equal(IntegrationTestSeeder.TestPrinterId, print.PrinterId);
    }

    [Fact]
    public async Task Webhook_Started_HumanizesFilenameForTitle()
    {
        // Arrange - underscored filename should be humanized into a title
        var filename = UniqueFileName("my_cool_benchy_model");
        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var request = CreateAuthenticatedWebhookRequest(messageDto);

        // Act
        var response = await _httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert - title should be humanized and title-cased
        var print = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(print);
        Assert.NotNull(print.Title);
        Assert.NotEmpty(print.Title);
        // The humanized title should contain "My" (capitalized)
        Assert.Contains("My", print.Title);
    }

    [Fact]
    public async Task Webhook_Started_SetsStartDate()
    {
        // Arrange
        var filename = UniqueFileName("started_date_test");
        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var request = CreateAuthenticatedWebhookRequest(messageDto);

        // Act
        var beforeSend = DateTimeOffset.UtcNow;
        var response = await _httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert - StartDate should be set to approximately now
        var print = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(print);
        Assert.NotNull(print.StartDate);
        Assert.True(print.StartDate >= beforeSend.AddSeconds(-5), "StartDate should be close to current time");
    }

    [Fact]
    public async Task Webhook_Started_CreatesFilamentUsageEntry()
    {
        // Arrange
        var filename = UniqueFileName("started_filament_usage_test");
        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var request = CreateAuthenticatedWebhookRequest(messageDto);

        // Act
        var response = await _httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert - verify filament usage was created
        var print = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(print);
        Assert.NotNull(print.FilamentUsage);
        Assert.NotEmpty(print.FilamentUsage);
    }

    [Fact]
    public async Task Webhook_Started_WithSubdirectoryFilename_UsesOnlyFileName()
    {
        // Arrange - Moonraker may send paths with directories
        var filename = "subdirectory/" + UniqueFileName("nested_model");
        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var request = CreateAuthenticatedWebhookRequest(messageDto);

        // Act
        var response = await _httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert - FileName should be just the file name, not the full path
        var print = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(print);
        Assert.DoesNotContain("subdirectory", print.FileName);
    }

    [Fact]
    public async Task Webhook_Started_WithZeroPrinterId_ThrowsException()
    {
        // Arrange - PrinterId = 0 should cause an error
        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = UniqueFileName("invalid_printer_test"),
            PrinterId = 0,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var request = CreateAuthenticatedWebhookRequest(messageDto);

        // Act & Assert - controller throws Exception("Invalid PrinterId") which is unhandled
        // In TestServer, unhandled exceptions propagate to the test
        await Assert.ThrowsAsync<Exception>(async () =>
        {
            await _httpClient.SendAsync(request);
        });
    }

    [Fact]
    public async Task Webhook_Started_WithOtherUsersPrinter_ThrowsUserCannotAccessPrinterException()
    {
        // Arrange - create another user's printer
        var otherPrinterId = SeedOtherUserWithPrinter();

        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = UniqueFileName("access_denied_test"),
            PrinterId = otherPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var request = CreateAuthenticatedWebhookRequest(messageDto);

        // Act & Assert - controller throws UserCannotAccessPrinterException which is unhandled
        // In TestServer, unhandled exceptions propagate to the test
        await Assert.ThrowsAsync<PrintLogApi.Exceptions.UserCannotAccessPrinterException>(async () =>
        {
            await _httpClient.SendAsync(request);
        });
    }

    [Fact]
    public async Task Webhook_Started_WithAllowCommentsSetting_UsesSettingValue()
    {
        // Arrange - seed user setting for AllowComments (type 3)
        SeedUserSetting(3, "true");

        var filename = UniqueFileName("allow_comments_setting_test");
        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var request = CreateAuthenticatedWebhookRequest(messageDto);

        // Act
        var response = await _httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert - AllowComments should be true based on user setting
        var print = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(print);
        Assert.True(print.AllowComments);
    }

    [Fact]
    public async Task Webhook_Started_WithViewStatusSetting_UsesSettingValue()
    {
        // Arrange - seed user setting for default view status (type 1) as "Public"
        SeedUserSetting(1, "Public");

        var filename = UniqueFileName("view_status_setting_test");
        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var request = CreateAuthenticatedWebhookRequest(messageDto);

        // Act
        var response = await _httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert - ViewStatus should be Public based on user setting
        var print = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(print);
        Assert.Equal(PrintViewStatus.Public, print.ViewStatus);
    }

    [Fact]
    public async Task Webhook_Started_WithNoUserSettings_DefaultsToFalseAndPrivate()
    {
        // Arrange - clear user settings
        ClearUserSettings();

        var filename = UniqueFileName("no_settings_defaults_test");
        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var request = CreateAuthenticatedWebhookRequest(messageDto);

        // Act
        var response = await _httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert - should default to false for AllowComments and Private for ViewStatus
        var print = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(print);
        Assert.False(print.AllowComments);
        Assert.Equal(PrintViewStatus.Private, print.ViewStatus);
    }

    [Fact]
    public async Task Webhook_Started_WithLongFilename_TruncatesTitle()
    {
        // Arrange - filename that would produce a title longer than 100 chars
        var longName = new string('a', 120);
        var filename = $"{longName}.gcode";
        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var request = CreateAuthenticatedWebhookRequest(messageDto);

        // Act
        var response = await _httpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert - title should be truncated to 100 characters max
        var print = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(print);
        Assert.True(print.Title!.Length <= 100, $"Title should be 100 chars or less, was {print.Title!.Length}");
    }

    #endregion

    #region Complete Event Tests

    [Fact]
    public async Task Webhook_Complete_UpdatesPrintToSuccess()
    {
        // Arrange - first create a print via the started event
        var filename = UniqueFileName("complete_success_test");
        var startedDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var startRequest = CreateAuthenticatedWebhookRequest(startedDto);
        var startResponse = await _httpClient.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);

        // Get the created print
        var createdPrint = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(createdPrint);
        Assert.Equal(PrintStatus.Printing, createdPrint.Status);

        // Act - send complete event
        var completeDto = new PrintEventMessageDto
        {
            EventName = "complete",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 5000, // 5000mm = 5m
            PrintDuration = 3600,
            TotalDuration = 3700
        };

        var completeRequest = CreateAuthenticatedWebhookRequest(completeDto);
        var completeResponse = await _httpClient.SendAsync(completeRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

        // Verify the print was updated - need to re-fetch from DB
        var updatedPrint = FindPrintByFileNameInDb(filename)!;
        Assert.Equal(PrintStatus.Success, updatedPrint.Status);
    }

    [Fact]
    public async Task Webhook_Complete_SetsPrintTimeFromTotalDuration()
    {
        // Arrange - create a print
        var filename = UniqueFileName("complete_duration_test");
        var startedDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var startRequest = CreateAuthenticatedWebhookRequest(startedDto);
        await _httpClient.SendAsync(startRequest);

        var createdPrint = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(createdPrint);

        // Act - complete with specific total duration
        var completeDto = new PrintEventMessageDto
        {
            EventName = "complete",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 1000,
            PrintDuration = 3500.7,
            TotalDuration = 3700.3
        };

        var completeRequest = CreateAuthenticatedWebhookRequest(completeDto);
        await _httpClient.SendAsync(completeRequest);

        // Assert - PrintTimeInSeconds should come from TotalDuration (rounded)
        var updatedPrint = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(updatedPrint);
        Assert.Equal(3700, updatedPrint.PrintTimeInSeconds);
    }

    [Fact]
    public async Task Webhook_Complete_UpdatesFilamentUsage()
    {
        // Arrange - create a print
        var filename = UniqueFileName("complete_filament_test");
        var startedDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var startRequest = CreateAuthenticatedWebhookRequest(startedDto);
        await _httpClient.SendAsync(startRequest);

        var createdPrint = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(createdPrint);

        // Act - complete with filament usage (in mm from Moonraker)
        var filamentUsedMm = 5000.0; // 5000mm
        var completeDto = new PrintEventMessageDto
        {
            EventName = "complete",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = filamentUsedMm,
            PrintDuration = 1800,
            TotalDuration = 1900
        };

        var completeRequest = CreateAuthenticatedWebhookRequest(completeDto);
        await _httpClient.SendAsync(completeRequest);

        // Assert - verify filament usage was updated
        var updatedPrint = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(updatedPrint);
        Assert.NotNull(updatedPrint.FilamentUsage);
        Assert.NotEmpty(updatedPrint.FilamentUsage);

        // Filament length should be converted from mm to m (5000mm / 1000 = 5m)
        var expectedLengthInM = Math.Round(filamentUsedMm / 1000, 3);
        Assert.Equal(expectedLengthInM, updatedPrint.FilamentUsage.First().LengthInM);
    }

    [Fact]
    public async Task Webhook_Complete_NoMatchingPrint_ReturnsOk()
    {
        // Arrange - complete event for a file that was never "started"
        var completeDto = new PrintEventMessageDto
        {
            EventName = "complete",
            Filename = UniqueFileName("nonexistent_print_complete"),
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 1000,
            PrintDuration = 3600,
            TotalDuration = 3700
        };

        var request = CreateAuthenticatedWebhookRequest(completeDto);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert - controller returns Ok even when no matching print is found
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    #endregion

    #region Error Event Tests

    [Fact]
    public async Task Webhook_Error_UpdatesPrintToFailed()
    {
        // Arrange - create a print
        var filename = UniqueFileName("error_failed_test");
        var startedDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var startRequest = CreateAuthenticatedWebhookRequest(startedDto);
        await _httpClient.SendAsync(startRequest);

        var createdPrint = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(createdPrint);

        // Act - send error event
        var errorDto = new PrintEventMessageDto
        {
            EventName = "error",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 2000,
            PrintDuration = 1200.5,
            TotalDuration = 1300
        };

        var errorRequest = CreateAuthenticatedWebhookRequest(errorDto);
        var errorResponse = await _httpClient.SendAsync(errorRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, errorResponse.StatusCode);

        var updatedPrint = FindPrintByFileNameInDb(filename)!;
        Assert.Equal(PrintStatus.Failed, updatedPrint.Status);
    }

    [Fact]
    public async Task Webhook_Error_SetsPrintTimeFromPrintDuration()
    {
        // Arrange - create a print
        var filename = UniqueFileName("error_duration_test");
        var startedDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var startRequest = CreateAuthenticatedWebhookRequest(startedDto);
        await _httpClient.SendAsync(startRequest);

        var createdPrint = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(createdPrint);

        // Act - send error with specific print duration
        var errorDto = new PrintEventMessageDto
        {
            EventName = "error",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 500,
            PrintDuration = 900.7,
            TotalDuration = 1000
        };

        var errorRequest = CreateAuthenticatedWebhookRequest(errorDto);
        await _httpClient.SendAsync(errorRequest);

        // Assert - error handler uses PrintDuration (not TotalDuration)
        var updatedPrint = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(updatedPrint);
        Assert.Equal(901, updatedPrint.PrintTimeInSeconds);
    }

    [Fact]
    public async Task Webhook_Error_NoMatchingPrint_ReturnsOk()
    {
        // Arrange - error event for a file that was never "started"
        var errorDto = new PrintEventMessageDto
        {
            EventName = "error",
            Filename = UniqueFileName("nonexistent_print_error"),
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 1000,
            PrintDuration = 600,
            TotalDuration = 700
        };

        var request = CreateAuthenticatedWebhookRequest(errorDto);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert - controller returns Ok even when no matching print is found
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    #endregion

    #region Cancelled Event Tests

    [Fact]
    public async Task Webhook_Cancelled_UpdatesPrintToFailed()
    {
        // Arrange - create a print
        var filename = UniqueFileName("cancelled_failed_test");
        var startedDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var startRequest = CreateAuthenticatedWebhookRequest(startedDto);
        await _httpClient.SendAsync(startRequest);

        var createdPrint = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(createdPrint);

        // Act - send cancelled event
        var cancelledDto = new PrintEventMessageDto
        {
            EventName = "cancelled",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 1500,
            PrintDuration = 800,
            TotalDuration = 900
        };

        var cancelledRequest = CreateAuthenticatedWebhookRequest(cancelledDto);
        var cancelledResponse = await _httpClient.SendAsync(cancelledRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, cancelledResponse.StatusCode);

        var updatedPrint = FindPrintByFileNameInDb(filename)!;
        Assert.Equal(PrintStatus.Failed, updatedPrint.Status);
    }

    [Fact]
    public async Task Webhook_Cancelled_NoMatchingPrint_ReturnsOk()
    {
        // Arrange - cancelled event for a file that was never "started"
        var cancelledDto = new PrintEventMessageDto
        {
            EventName = "cancelled",
            Filename = UniqueFileName("nonexistent_print_cancelled"),
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 500,
            PrintDuration = 300,
            TotalDuration = 400
        };

        var request = CreateAuthenticatedWebhookRequest(cancelledDto);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    #endregion

    #region Unknown Event Tests

    [Fact]
    public async Task Webhook_UnknownEvent_ReturnsOk()
    {
        // Arrange - send an event the controller doesn't explicitly handle
        var messageDto = new PrintEventMessageDto
        {
            EventName = "paused",
            Filename = UniqueFileName("unknown_event_test"),
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var request = CreateAuthenticatedWebhookRequest(messageDto);

        // Act
        var response = await _httpClient.SendAsync(request);

        // Assert - unhandled events are silently ignored and return Ok
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    #endregion

    #region Full Lifecycle Tests

    [Fact]
    public async Task Webhook_FullLifecycle_StartedThenCompleted()
    {
        // Arrange
        var filename = UniqueFileName("lifecycle_complete_test");

        // Step 1: Start print
        var startedDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var startRequest = CreateAuthenticatedWebhookRequest(startedDto);
        var startResponse = await _httpClient.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);

        // Verify print was created in Printing status
        var createdPrint = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(createdPrint);
        Assert.Equal(PrintStatus.Printing, createdPrint.Status);

        // Step 2: Complete print
        var completeDto = new PrintEventMessageDto
        {
            EventName = "complete",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 10000, // 10m
            PrintDuration = 7000,
            TotalDuration = 7200
        };

        var completeRequest = CreateAuthenticatedWebhookRequest(completeDto);
        var completeResponse = await _httpClient.SendAsync(completeRequest);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

        // Verify print was updated to Success
        var completedPrint = FindPrintByFileNameInDb(filename)!;
        Assert.Equal(PrintStatus.Success, completedPrint.Status);
        Assert.Equal(7200, completedPrint.PrintTimeInSeconds);
        Assert.NotNull(completedPrint.FilamentUsage);
        Assert.NotEmpty(completedPrint.FilamentUsage);
        Assert.Equal(10.0, completedPrint.FilamentUsage.First().LengthInM);
    }

    [Fact]
    public async Task Webhook_FullLifecycle_StartedThenFailed()
    {
        // Arrange
        var filename = UniqueFileName("lifecycle_failed_test");

        // Step 1: Start print
        var startedDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var startRequest = CreateAuthenticatedWebhookRequest(startedDto);
        var startResponse = await _httpClient.SendAsync(startRequest);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);

        var createdPrint = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(createdPrint);

        // Step 2: Error during print
        var errorDto = new PrintEventMessageDto
        {
            EventName = "error",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 3000, // 3m used before error
            PrintDuration = 2400.0,
            TotalDuration = 2500.0
        };

        var errorRequest = CreateAuthenticatedWebhookRequest(errorDto);
        var errorResponse = await _httpClient.SendAsync(errorRequest);
        Assert.Equal(HttpStatusCode.OK, errorResponse.StatusCode);

        // Verify print was updated to Failed
        var failedPrint = FindPrintByFileNameInDb(filename)!;
        Assert.Equal(PrintStatus.Failed, failedPrint.Status);
        Assert.Equal(2400, failedPrint.PrintTimeInSeconds);
        Assert.NotNull(failedPrint.FilamentUsage);
        Assert.NotEmpty(failedPrint.FilamentUsage);
        Assert.Equal(3.0, failedPrint.FilamentUsage.First().LengthInM);
    }

    [Fact]
    public async Task Webhook_FullLifecycle_StartedThenCancelled()
    {
        // Arrange
        var filename = UniqueFileName("lifecycle_cancelled_test");

        // Step 1: Start print
        var startedDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var startRequest = CreateAuthenticatedWebhookRequest(startedDto);
        await _httpClient.SendAsync(startRequest);

        var createdPrint = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(createdPrint);

        // Step 2: Cancel print
        var cancelledDto = new PrintEventMessageDto
        {
            EventName = "cancelled",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 1000,
            PrintDuration = 600,
            TotalDuration = 650
        };

        var cancelledRequest = CreateAuthenticatedWebhookRequest(cancelledDto);
        var cancelledResponse = await _httpClient.SendAsync(cancelledRequest);
        Assert.Equal(HttpStatusCode.OK, cancelledResponse.StatusCode);

        // Verify print was updated to Failed (cancelled maps to Failed)
        var cancelledPrint = FindPrintByFileNameInDb(filename)!;
        Assert.Equal(PrintStatus.Failed, cancelledPrint.Status);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public async Task Webhook_Complete_MatchesByFileNameAndPrinterIdAndStatus()
    {
        // Arrange - create a print
        var filename = UniqueFileName("matching_logic_test");

        // Create print on printer 1
        var startedDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var startRequest = CreateAuthenticatedWebhookRequest(startedDto);
        await _httpClient.SendAsync(startRequest);

        var createdPrint = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(createdPrint);

        // Act - complete the print on the correct printer
        var completeDto = new PrintEventMessageDto
        {
            EventName = "complete",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 2000,
            PrintDuration = 1000,
            TotalDuration = 1100
        };

        var completeRequest = CreateAuthenticatedWebhookRequest(completeDto);
        var completeResponse = await _httpClient.SendAsync(completeRequest);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

        // Assert - the print should be updated
        var updatedPrint = FindPrintByFileNameInDb(filename)!;
        Assert.Equal(PrintStatus.Success, updatedPrint.Status);
    }

    [Fact]
    public async Task Webhook_ResponseContainsProcessedDto()
    {
        // Arrange
        var filename = UniqueFileName("response_dto_test");
        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var request = CreateAuthenticatedWebhookRequest(messageDto);

        // Act
        var response = await _httpClient.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        // Assert - the response should contain the processed DTO
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("started", responseContent);
        Assert.Contains(System.IO.Path.GetFileName(filename), responseContent);
    }

    [Fact]
    public async Task Webhook_Started_StoresNullEstimate_NotZero()
    {
        // The start payload carries no estimate, and this used to hardcode 0. A zero estimate is
        // strictly worse than a null: it looks recorded, so no read-side fallback can recover
        // from it — the print's duration is then lost forever.
        var filename = UniqueFileName("started_null_estimate_test");
        var messageDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };

        var response = await _httpClient.SendAsync(CreateAuthenticatedWebhookRequest(messageDto));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var print = FindPrintByFileNameInDb(filename)!;
        Assert.NotNull(print);
        Assert.Null(print.EstimatedPrintTimeInSeconds);
    }

    [Fact]
    public async Task Webhook_Complete_WithNoDuration_StoresNull_NotZero()
    {
        // PrintEventMessageDto.TotalDuration is a non-nullable double, so "no duration reported"
        // arrives as 0.0 — exactly the value that must NOT be persisted as a measurement.
        var filename = UniqueFileName("complete_zero_duration_test");
        var startDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };
        await _httpClient.SendAsync(CreateAuthenticatedWebhookRequest(startDto));

        var completeDto = new PrintEventMessageDto
        {
            EventName = "complete",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 5000,
            PrintDuration = 0,
            TotalDuration = 0
        };
        var response = await _httpClient.SendAsync(CreateAuthenticatedWebhookRequest(completeDto));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var print = FindPrintByFileNameInDb(filename)!;
        Assert.Equal(PrintStatus.Success, print.Status);
        Assert.Null(print.PrintTimeInSeconds);
    }

    [Fact]
    public async Task Webhook_Complete_WithSubSecondDuration_StoresNull_NotZero()
    {
        // 0.3 > 0 is TRUE, but Math.Round(0.3) is 0. Validating positivity BEFORE rounding would
        // therefore persist the very zero this change exists to eliminate. This test is the only
        // thing that distinguishes rounding-then-checking from checking-then-rounding.
        var filename = UniqueFileName("complete_subsecond_duration_test");
        var startDto = new PrintEventMessageDto
        {
            EventName = "started",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 0,
            PrintDuration = 0,
            TotalDuration = 0
        };
        await _httpClient.SendAsync(CreateAuthenticatedWebhookRequest(startDto));

        var completeDto = new PrintEventMessageDto
        {
            EventName = "complete",
            Filename = filename,
            PrinterId = IntegrationTestSeeder.TestPrinterId,
            FilamentUsed = 5000,
            PrintDuration = 0.3,
            TotalDuration = 0.3
        };
        var response = await _httpClient.SendAsync(CreateAuthenticatedWebhookRequest(completeDto));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var print = FindPrintByFileNameInDb(filename)!;
        Assert.Null(print.PrintTimeInSeconds);
    }

    #endregion
}
