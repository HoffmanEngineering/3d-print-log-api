using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Octoprint;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers
{
    public class OctoprintControllerTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly HttpClient _httpClient;
        private readonly CustomWebApplicationFactory _factory;

        public OctoprintControllerTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
            _httpClient = factory.CreateClient();
        }

        #region Helpers

        /// <summary>
        /// Creates a multipart form content for Octoprint webhook.
        /// The Octoprint webhook uses [FromForm] with JSON-encoded nested objects.
        /// </summary>
        private static MultipartFormDataContent CreateWebhookFormContent(
            string topic,
            string deviceIdentifier,
            string fileName = null,
            double? estimatedPrintTime = null,
            double? printTime = null,
            string fileHash = null,
            long? currentTime = null,
            OctoprintWebhookMetaAnalysisFilamentDto filamentData = null)
        {
            var content = new MultipartFormDataContent();

            content.Add(new StringContent(topic), "Topic");
            content.Add(new StringContent(deviceIdentifier), "DeviceIdentifier");
            content.Add(new StringContent((currentTime ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString()), "CurrentTime");

            // Extra DTO (JSON)
            var extra = new OctoprintWebhookExtraDto
            {
                Name = fileName,
                Path = fileName,
                Time = printTime
            };
            content.Add(new StringContent(JsonSerializer.Serialize(extra)), "Extra");

            // Job DTO (JSON)
            if (fileName != null)
            {
                var job = new OctoprintWebhookJobDto
                {
                    File = new OctoprintWebhookJobFileDto { Name = fileName },
                    EstimatedPrintTime = estimatedPrintTime,
                    AveragePrintTime = estimatedPrintTime,
                    Filament = filamentData
                };
                content.Add(new StringContent(JsonSerializer.Serialize(job)), "Job");
            }

            // Meta DTO (JSON)
            if (fileHash != null || filamentData != null)
            {
                var meta = new OctoprintWebhookMetaDto
                {
                    Hash = fileHash,
                    Analysis = filamentData != null ? new OctoprintWebhookMetaAnalysisDto { filament = filamentData } : null
                };
                content.Add(new StringContent(JsonSerializer.Serialize(meta)), "Meta");
            }

            return content;
        }

        /// <summary>
        /// Creates a test webhook payload for Octoprint.
        /// </summary>
        private static MultipartFormDataContent CreateTestWebhookFormContent(string deviceIdentifier)
        {
            var content = new MultipartFormDataContent();

            content.Add(new StringContent("Test"), "Topic");
            content.Add(new StringContent(deviceIdentifier), "DeviceIdentifier");
            content.Add(new StringContent(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()), "CurrentTime");

            // Extra with example.gcode triggers test mode
            var extra = new OctoprintWebhookExtraDto
            {
                Name = "example.gcode",
                Path = "example.gcode"
            };
            content.Add(new StringContent(JsonSerializer.Serialize(extra)), "Extra");

            return content;
        }

        private HttpRequestMessage CreateAuthenticatedWebhookRequest(MultipartFormDataContent content)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Octoprint");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            request.Content = content;
            return request;
        }

        /// <summary>
        /// Finds a print by filename directly from the database.
        /// </summary>
        private Print FindPrintByFileNameInDb(string expectedFileName)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            return db.Prints
                .Include(p => p.FilamentUsage)
                .Where(p => p.FileName == expectedFileName && p.CreatedById == IntegrationTestSeeder.TestUserId)
                .OrderByDescending(p => p.Id)
                .FirstOrDefault();
        }

        /// <summary>
        /// Finds a print by file hash directly from the database.
        /// </summary>
        private Print FindPrintByFileHashInDb(string hexHash)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            var hashBytes = StringToByteArray(hexHash);

            return db.Prints
                .Include(p => p.FilamentUsage)
                .Where(p => p.FileHash == hashBytes && p.CreatedById == IntegrationTestSeeder.TestUserId)
                .OrderByDescending(p => p.Id)
                .FirstOrDefault();
        }

        private static byte[] StringToByteArray(string hex)
        {
            return Enumerable.Range(0, hex.Length)
                             .Where(x => x % 2 == 0)
                             .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                             .ToArray();
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
        /// Clears user settings for the test user.
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

            var existingUser = db.Users.FirstOrDefault(u => u.OAuthUserId == "auth0|other-user-octoprint");
            if (existingUser != null)
            {
                var existingPrinter = db.Printers.FirstOrDefault(p => p.UserId == existingUser.Id);
                return existingPrinter?.Id ?? 0;
            }

            var otherUser = new User
            {
                OAuthUserId = "auth0|other-user-octoprint",
                ViewStatus = User.ProfileViewStatus.Public
            };
            db.Users.Add(otherUser);
            db.SaveChanges();

            var otherPrinter = new Printer
            {
                Name = "Other User's Octoprint Printer",
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

        /// <summary>
        /// Generates a unique SHA1-like hash (40 hex chars).
        /// </summary>
        private static string UniqueFileHash()
        {
            return Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        #endregion

        #region Authentication Tests

        [Fact]
        public async Task Webhook_NotAuthenticated_ReturnsUnauthorized()
        {
            // Arrange - no auth header
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: UniqueFileName("test_model"));

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Octoprint");
            request.Content = content;

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        #endregion

        #region Test Webhook Tests

        [Fact]
        public async Task Webhook_TestWebhook_ReturnsSuccessMessage()
        {
            // Arrange - test webhook has Extra.Name = "example.gcode"
            var content = CreateTestWebhookFormContent(IntegrationTestSeeder.TestPrinterId.ToString());
            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Webhook Connection to 3D Print Log is Good", responseContent);
            Assert.Contains("Test Printer 1", responseContent); // The seeded printer name
        }

        [Fact]
        public async Task Webhook_TestWebhook_WithInvalidPrinter_ReturnsBadRequest()
        {
            // Arrange - test webhook with non-existent printer
            var content = CreateTestWebhookFormContent("999999");
            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Webhook_TestWebhook_WithOtherUsersPrinter_ReturnsBadRequest()
        {
            // Arrange - create another user's printer
            var otherPrinterId = SeedOtherUserWithPrinter();

            var content = CreateTestWebhookFormContent(otherPrinterId.ToString());
            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var responseContent = await response.Content.ReadAsStringAsync();
            Assert.Contains("Printer does not belong to current user", responseContent);
        }

        [Fact]
        public async Task Webhook_TestWebhook_WithInvalidDeviceIdentifier_ReturnsBadRequest()
        {
            // Arrange - non-numeric device identifier
            var content = CreateTestWebhookFormContent("not-a-number");
            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var responseContent = await response.Content.ReadAsStringAsync();
            Assert.Contains("No Printer Id found", responseContent);
        }

        #endregion

        #region Print Started Tests

        [Fact]
        public async Task Webhook_PrintStarted_ReturnsOk()
        {
            // Arrange
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: UniqueFileName("print_started_test"),
                estimatedPrintTime: 3600);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task Webhook_PrintStarted_CreatesNewPrint()
        {
            // Arrange
            var fileName = UniqueFileName("creates_print_test");
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 7200);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Assert - verify a print was created
            var print = FindPrintByFileNameInDb(fileName);
            Assert.NotNull(print);
            Assert.Equal(PrintStatus.Printing, print.Status);
            Assert.Equal(fileName, print.FileName);
            Assert.Equal(IntegrationTestSeeder.TestPrinterId, print.PrinterId);
        }

        [Fact]
        public async Task Webhook_PrintStarted_SetsTitleFromFileName()
        {
            // Arrange
            var fileName = UniqueFileName("my_awesome_print");
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Assert - title should be set from filename
            var print = FindPrintByFileNameInDb(fileName);
            Assert.NotNull(print);
            Assert.Equal(fileName, print.Title);
        }

        [Fact]
        public async Task Webhook_PrintStarted_SetsEstimatedPrintTime()
        {
            // Arrange
            var fileName = UniqueFileName("estimated_time_test");
            var estimatedTime = 5400.7;
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: estimatedTime);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Assert - estimated time should be rounded
            var print = FindPrintByFileNameInDb(fileName);
            Assert.NotNull(print);
            Assert.Equal(5401, print.EstimatedPrintTimeInSeconds);
        }

        [Fact]
        public async Task Webhook_PrintStarted_SetsStartDateFromCurrentTime()
        {
            // Arrange
            var fileName = UniqueFileName("start_date_test");
            var unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                currentTime: unixTime);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Assert - StartDate should be set from CurrentTime
            var print = FindPrintByFileNameInDb(fileName);
            Assert.NotNull(print);
            Assert.NotNull(print.StartDate);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(unixTime), print.StartDate);
        }

        [Fact]
        public async Task Webhook_PrintStarted_SetsFileHash()
        {
            // Arrange
            var fileName = UniqueFileName("file_hash_test");
            var fileHash = UniqueFileHash();
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                fileHash: fileHash);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Assert - file hash should be stored
            var print = FindPrintByFileHashInDb(fileHash);
            Assert.NotNull(print);
            Assert.Equal(fileName, print.FileName);
        }

        [Fact]
        public async Task Webhook_PrintStarted_WithInvalidDeviceIdentifier_ThrowsException()
        {
            // Arrange - non-numeric device identifier
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: "not-a-number",
                fileName: UniqueFileName("invalid_device_test"));

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act & Assert - controller throws Exception("Invalid Device Identifier")
            await Assert.ThrowsAsync<Exception>(async () =>
            {
                await _httpClient.SendAsync(request);
            });
        }

        [Fact]
        public async Task Webhook_PrintStarted_WithOtherUsersPrinter_ThrowsUserCannotAccessPrinterException()
        {
            // Arrange
            var otherPrinterId = SeedOtherUserWithPrinter();

            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: otherPrinterId.ToString(),
                fileName: UniqueFileName("access_denied_test"));

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act & Assert
            await Assert.ThrowsAsync<PrintLogApi.Exceptions.UserCannotAccessPrinterException>(async () =>
            {
                await _httpClient.SendAsync(request);
            });
        }

        [Fact]
        public async Task Webhook_PrintStarted_WithAllowCommentsSetting_UsesSettingValue()
        {
            // Arrange
            SeedUserSetting(3, "true");

            var fileName = UniqueFileName("allow_comments_test");
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Assert
            var print = FindPrintByFileNameInDb(fileName);
            Assert.NotNull(print);
            Assert.True(print.AllowComments);
        }

        [Fact]
        public async Task Webhook_PrintStarted_WithViewStatusSetting_UsesSettingValue()
        {
            // Arrange
            SeedUserSetting(1, "Public");

            var fileName = UniqueFileName("view_status_test");
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Assert
            var print = FindPrintByFileNameInDb(fileName);
            Assert.NotNull(print);
            Assert.Equal(PrintViewStatus.Public, print.ViewStatus);
        }

        [Fact]
        public async Task Webhook_PrintStarted_WithNoUserSettings_DefaultsToFalseAndPrivate()
        {
            // Arrange
            ClearUserSettings();

            var fileName = UniqueFileName("no_settings_test");
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Assert
            var print = FindPrintByFileNameInDb(fileName);
            Assert.NotNull(print);
            Assert.False(print.AllowComments);
            Assert.Equal(PrintViewStatus.Private, print.ViewStatus);
        }

        [Fact]
        public async Task Webhook_PrintStarted_WithFilamentData_CreatesFilamentUsage()
        {
            // Arrange
            var fileName = UniqueFileName("filament_usage_test");
            var filamentData = new OctoprintWebhookMetaAnalysisFilamentDto
            {
                tool0 = new OctoprintWebhookFilamentUsageDto { length = 5000, volumn = 12.5 }
            };

            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                filamentData: filamentData);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Assert
            var print = FindPrintByFileNameInDb(fileName);
            Assert.NotNull(print);
            Assert.NotNull(print.FilamentUsage);
            Assert.NotEmpty(print.FilamentUsage);
            // 5000mm / 1000 = 5m
            Assert.Equal(5.0, print.FilamentUsage.First().EstimatedLengthInM);
        }

        #endregion

        #region Print Done Tests

        [Fact]
        public async Task Webhook_PrintDone_UpdatesPrintToSuccess()
        {
            // Arrange - first create a print
            var fileName = UniqueFileName("print_done_test");
            var fileHash = UniqueFileHash();
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                fileHash: fileHash);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            var startResponse = await _httpClient.SendAsync(startRequest);
            Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);

            var createdPrint = FindPrintByFileHashInDb(fileHash);
            Assert.NotNull(createdPrint);
            Assert.Equal(PrintStatus.Printing, createdPrint.Status);

            // Act - send Print Done
            var doneContent = CreateWebhookFormContent(
                topic: "Print Done",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: 3500,
                fileHash: fileHash);

            var doneRequest = CreateAuthenticatedWebhookRequest(doneContent);
            var doneResponse = await _httpClient.SendAsync(doneRequest);

            // Assert
            Assert.Equal(HttpStatusCode.OK, doneResponse.StatusCode);

            var updatedPrint = FindPrintByFileHashInDb(fileHash);
            Assert.Equal(PrintStatus.Success, updatedPrint.Status);
        }

        [Fact]
        public async Task Webhook_PrintDone_SetsPrintTime()
        {
            // Arrange - first create a print
            var fileName = UniqueFileName("print_done_time_test");
            var fileHash = UniqueFileHash();
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                fileHash: fileHash);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            await _httpClient.SendAsync(startRequest);

            // Act - send Print Done with specific time
            var printTime = 3456.7;
            var doneContent = CreateWebhookFormContent(
                topic: "Print Done",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: printTime,
                fileHash: fileHash);

            var doneRequest = CreateAuthenticatedWebhookRequest(doneContent);
            await _httpClient.SendAsync(doneRequest);

            // Assert
            var updatedPrint = FindPrintByFileHashInDb(fileHash);
            Assert.NotNull(updatedPrint);
            Assert.Equal(3457, updatedPrint.PrintTimeInSeconds);
        }

        [Fact]
        public async Task Webhook_PrintDone_MatchesByFileName_WhenNoHash()
        {
            // Arrange - create a print without hash
            var fileName = UniqueFileName("print_done_by_name_test");
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            await _httpClient.SendAsync(startRequest);

            var createdPrint = FindPrintByFileNameInDb(fileName);
            Assert.NotNull(createdPrint);

            // Act - send Print Done without hash (will match by filename)
            var doneContent = CreateWebhookFormContent(
                topic: "Print Done",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: 2000);

            var doneRequest = CreateAuthenticatedWebhookRequest(doneContent);
            await _httpClient.SendAsync(doneRequest);

            // Assert
            var updatedPrint = FindPrintByFileNameInDb(fileName);
            Assert.Equal(PrintStatus.Success, updatedPrint.Status);
        }

        [Fact]
        public async Task Webhook_PrintDone_NoMatchingPrint_ReturnsOk()
        {
            // Arrange - Print Done for a file that was never started
            var content = CreateWebhookFormContent(
                topic: "Print Done",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: UniqueFileName("nonexistent_print"),
                printTime: 3600);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert - returns Ok even when no matching print found
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        #endregion

        #region Print Failed Tests

        [Fact]
        public async Task Webhook_PrintFailed_UpdatesPrintToFailed()
        {
            // Arrange - first create a print
            var fileName = UniqueFileName("print_failed_test");
            var fileHash = UniqueFileHash();
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                fileHash: fileHash);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            await _httpClient.SendAsync(startRequest);

            // Act - send Print Failed
            var failedContent = CreateWebhookFormContent(
                topic: "Print Failed",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: 1200,
                fileHash: fileHash);

            var failedRequest = CreateAuthenticatedWebhookRequest(failedContent);
            var failedResponse = await _httpClient.SendAsync(failedRequest);

            // Assert
            Assert.Equal(HttpStatusCode.OK, failedResponse.StatusCode);

            var updatedPrint = FindPrintByFileHashInDb(fileHash);
            Assert.Equal(PrintStatus.Failed, updatedPrint.Status);
        }

        [Fact]
        public async Task Webhook_PrintFailed_SetsPrintTime()
        {
            // Arrange
            var fileName = UniqueFileName("print_failed_time_test");
            var fileHash = UniqueFileHash();
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                fileHash: fileHash);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            await _httpClient.SendAsync(startRequest);

            // Act
            var printTime = 800.7;
            var failedContent = CreateWebhookFormContent(
                topic: "Print Failed",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: printTime,
                fileHash: fileHash);

            var failedRequest = CreateAuthenticatedWebhookRequest(failedContent);
            await _httpClient.SendAsync(failedRequest);

            // Assert - Math.Round with MidpointRounding.AwayFromZero not used, so 800.7 -> 801
            var updatedPrint = FindPrintByFileHashInDb(fileHash);
            Assert.NotNull(updatedPrint);
            Assert.Equal(801, updatedPrint.PrintTimeInSeconds);
        }

        [Fact]
        public async Task Webhook_PrintFailed_NoMatchingPrint_ReturnsOk()
        {
            // Arrange
            var content = CreateWebhookFormContent(
                topic: "Print Failed",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: UniqueFileName("nonexistent_print"),
                printTime: 600);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        #endregion

        #region Error Event Tests

        [Fact]
        public async Task Webhook_Error_UpdatesPrintToFailed()
        {
            // Arrange - first create a print
            var fileName = UniqueFileName("error_test");
            var fileHash = UniqueFileHash();
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                fileHash: fileHash);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            await _httpClient.SendAsync(startRequest);

            // Act - send Error
            var errorContent = CreateWebhookFormContent(
                topic: "Error",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: 500,
                fileHash: fileHash);

            var errorRequest = CreateAuthenticatedWebhookRequest(errorContent);
            var errorResponse = await _httpClient.SendAsync(errorRequest);

            // Assert
            Assert.Equal(HttpStatusCode.OK, errorResponse.StatusCode);

            var updatedPrint = FindPrintByFileHashInDb(fileHash);
            Assert.Equal(PrintStatus.Failed, updatedPrint.Status);
        }

        #endregion

        #region Unknown Event Tests

        [Fact]
        public async Task Webhook_UnknownTopic_ReturnsOk()
        {
            // Arrange - send an unknown topic
            var content = CreateWebhookFormContent(
                topic: "Print Paused",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: UniqueFileName("unknown_topic_test"));

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);

            // Assert - unknown topics are silently ignored
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        #endregion

        #region Full Lifecycle Tests

        [Fact]
        public async Task Webhook_FullLifecycle_StartedThenDone()
        {
            // Arrange
            var fileName = UniqueFileName("lifecycle_done_test");
            var fileHash = UniqueFileHash();

            // Step 1: Start print
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                fileHash: fileHash);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            var startResponse = await _httpClient.SendAsync(startRequest);
            Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);

            var createdPrint = FindPrintByFileHashInDb(fileHash);
            Assert.NotNull(createdPrint);
            Assert.Equal(PrintStatus.Printing, createdPrint.Status);

            // Step 2: Complete print
            var doneContent = CreateWebhookFormContent(
                topic: "Print Done",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: 3400,
                fileHash: fileHash);

            var doneRequest = CreateAuthenticatedWebhookRequest(doneContent);
            var doneResponse = await _httpClient.SendAsync(doneRequest);
            Assert.Equal(HttpStatusCode.OK, doneResponse.StatusCode);

            var completedPrint = FindPrintByFileHashInDb(fileHash);
            Assert.Equal(PrintStatus.Success, completedPrint.Status);
            Assert.Equal(3400, completedPrint.PrintTimeInSeconds);
        }

        [Fact]
        public async Task Webhook_FullLifecycle_StartedThenFailed()
        {
            // Arrange
            var fileName = UniqueFileName("lifecycle_failed_test");
            var fileHash = UniqueFileHash();

            // Step 1: Start print
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 7200,
                fileHash: fileHash);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            await _httpClient.SendAsync(startRequest);

            var createdPrint = FindPrintByFileHashInDb(fileHash);
            Assert.NotNull(createdPrint);

            // Step 2: Print fails
            var failedContent = CreateWebhookFormContent(
                topic: "Print Failed",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: 1800,
                fileHash: fileHash);

            var failedRequest = CreateAuthenticatedWebhookRequest(failedContent);
            await _httpClient.SendAsync(failedRequest);

            var failedPrint = FindPrintByFileHashInDb(fileHash);
            Assert.Equal(PrintStatus.Failed, failedPrint.Status);
            Assert.Equal(1800, failedPrint.PrintTimeInSeconds);
        }

        [Fact]
        public async Task Webhook_FullLifecycle_StartedThenError()
        {
            // Arrange
            var fileName = UniqueFileName("lifecycle_error_test");
            var fileHash = UniqueFileHash();

            // Step 1: Start print
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 5000,
                fileHash: fileHash);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            await _httpClient.SendAsync(startRequest);

            // Step 2: Error occurs
            var errorContent = CreateWebhookFormContent(
                topic: "Error",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: 600,
                fileHash: fileHash);

            var errorRequest = CreateAuthenticatedWebhookRequest(errorContent);
            await _httpClient.SendAsync(errorRequest);

            var errorPrint = FindPrintByFileHashInDb(fileHash);
            Assert.Equal(PrintStatus.Failed, errorPrint.Status);
            Assert.Equal(600, errorPrint.PrintTimeInSeconds);
        }

        #endregion

        #region Edge Case Tests

        [Fact]
        public async Task Webhook_PrintStarted_LongFileName_TruncatesTitle()
        {
            // Arrange - filename longer than 100 chars
            var longName = new string('a', 120) + ".gcode";
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: longName,
                estimatedPrintTime: 3600);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Assert - title should be truncated to 100 chars
            var print = FindPrintByFileNameInDb(longName);
            Assert.NotNull(print);
            Assert.True(print.Title.Length <= 100, $"Title should be 100 chars or less, was {print.Title.Length}");
        }

        [Fact]
        public async Task Webhook_PrintDone_MatchesByHash_PreferredOverFileName()
        {
            // Arrange - create a print with hash
            var fileName = UniqueFileName("hash_match_test");
            var fileHash = UniqueFileHash();
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                fileHash: fileHash);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            await _httpClient.SendAsync(startRequest);

            // Act - send Print Done with the hash
            var doneContent = CreateWebhookFormContent(
                topic: "Print Done",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: 3000,
                fileHash: fileHash);

            var doneRequest = CreateAuthenticatedWebhookRequest(doneContent);
            await _httpClient.SendAsync(doneRequest);

            // Assert - print should be found by hash and updated
            var updatedPrint = FindPrintByFileHashInDb(fileHash);
            Assert.NotNull(updatedPrint);
            Assert.Equal(PrintStatus.Success, updatedPrint.Status);
        }

        #endregion
    }
}
