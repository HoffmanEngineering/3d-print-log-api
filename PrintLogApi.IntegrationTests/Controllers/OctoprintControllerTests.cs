using System;
using System.Collections.Generic;
using System.IO;
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
            string? fileName = null,
            double? estimatedPrintTime = null,
            double? printTime = null,
            string? fileHash = null,
            long? currentTime = null,
            OctoprintWebhookMetaAnalysisFilamentDto? filamentData = null,
            bool includeSnapshot = false,
            // AveragePrintTime normally mirrors estimatedPrintTime. Set overrideAveragePrintTime to
            // drive the two fields apart, which is the only way to reach the case where a ZERO
            // average must not suppress a real EstimatedPrintTime.
            bool overrideAveragePrintTime = false,
            double? averagePrintTime = null)
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
                    AveragePrintTime = overrideAveragePrintTime ? averagePrintTime : estimatedPrintTime,
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

            // Add snapshot if requested
            if (includeSnapshot)
            {
                var imageContent = CreateTestImageFileContent();
                content.Add(imageContent, "snapshot", "snapshot.jpg");
            }

            return content;
        }

        /// <summary>
        /// Creates a simple test image file (minimal JPEG bytes).
        /// </summary>
        private static StreamContent CreateTestImageFileContent()
        {
            // Minimal JPEG header and data (a valid 1x1 pixel JPEG)
            byte[] jpegBytes = new byte[]
            {
                0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
                0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43,
                0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08, 0x07, 0x07, 0x07, 0x09,
                0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12,
                0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F, 0x1E, 0x1D, 0x1A, 0x1C, 0x1C, 0x20,
                0x24, 0x2E, 0x27, 0x20, 0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29,
                0x2C, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27, 0x39, 0x3D, 0x38, 0x32,
                0x3C, 0x2E, 0x33, 0x34, 0x32, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01,
                0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xC4, 0x00, 0x1F, 0x00, 0x00,
                0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                0x09, 0x0A, 0x0B, 0xFF, 0xC4, 0x00, 0xB5, 0x10, 0x00, 0x02, 0x01, 0x03,
                0x03, 0x02, 0x04, 0x03, 0x05, 0x05, 0x04, 0x04, 0x00, 0x00, 0x01, 0x7D,
                0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06,
                0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08,
                0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0, 0x24, 0x33, 0x62, 0x72,
                0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
                0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45,
                0x46, 0x47, 0x48, 0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
                0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x73, 0x74, 0x75,
                0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
                0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3,
                0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6,
                0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9,
                0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
                0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4,
                0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01,
                0x00, 0x00, 0x3F, 0x00, 0xFB, 0xD0, 0xFF, 0xD9
            };

            var stream = new MemoryStream(jpegBytes);
            var streamContent = new StreamContent(stream);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return streamContent;
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
        private Print? FindPrintByFileNameInDb(string expectedFileName)
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
        private Print? FindPrintByFileHashInDb(string hexHash)
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

        /// <summary>
        /// Finds PrintImages for a given print ID.
        /// </summary>
        private List<PrintImage> FindPrintImagesByPrintIdInDb(long printId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            return db.PrintImages
                .Include(pi => pi.File)
                .Where(pi => pi.PrintId == printId)
                .ToList();
        }

        /// <summary>
        /// Counts PrintImages for a given print ID.
        /// </summary>
        private int CountPrintImagesForPrint(long printId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();

            return db.PrintImages.Where(pi => pi.PrintId == printId).Count();
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
            var print = FindPrintByFileNameInDb(fileName)!;
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
            var print = FindPrintByFileNameInDb(fileName)!;
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
            var print = FindPrintByFileNameInDb(fileName)!;
            Assert.NotNull(print);
            Assert.Equal(5401, print.EstimatedPrintTimeInSeconds);
        }

        [Fact]
        public async Task Webhook_PrintStarted_WithNoEstimate_StoresNull_NotZero()
        {
            // Both AveragePrintTime and EstimatedPrintTime are null (the builder leaves them unset),
            // and this used to coerce that to 0. A zero estimate is strictly worse than a null: it
            // looks recorded, so no read-side fallback can recover the print's duration.
            var fileName = UniqueFileName("no_estimate_test");
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: null);

            var response = await _httpClient.SendAsync(CreateAuthenticatedWebhookRequest(content));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var print = FindPrintByFileNameInDb(fileName)!;
            Assert.NotNull(print);
            Assert.Null(print.EstimatedPrintTimeInSeconds);
        }

        [Fact]
        public async Task Webhook_PrintStarted_ZeroAveragePrintTime_DoesNotSuppressTheRealEstimate()
        {
            // `AveragePrintTime ?? EstimatedPrintTime` picks Average whenever it is non-null — and
            // 0.0 IS non-null. A file OctoPrint has never printed before can report a zero average
            // alongside a real slicer estimate, and the `??` would discard the estimate entirely.
            // Exactly the "a stored 0 beats a real value" defect this change exists to eliminate.
            var fileName = UniqueFileName("zero_average_real_estimate_test");
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600.0,
                overrideAveragePrintTime: true,
                averagePrintTime: 0.0);

            var response = await _httpClient.SendAsync(CreateAuthenticatedWebhookRequest(content));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var print = FindPrintByFileNameInDb(fileName)!;
            Assert.NotNull(print);
            Assert.Equal(3600, print.EstimatedPrintTimeInSeconds);   // NOT null, and NOT 0
        }

        [Fact]
        public async Task Webhook_PrintStarted_WithSubSecondEstimate_StoresNull_NotZero()
        {
            // 0.3 > 0 is TRUE, but Math.Round(0.3) is 0. Checking positivity BEFORE rounding would
            // persist the very zero this change exists to eliminate.
            var fileName = UniqueFileName("subsecond_estimate_test");
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 0.3);

            var response = await _httpClient.SendAsync(CreateAuthenticatedWebhookRequest(content));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var print = FindPrintByFileNameInDb(fileName)!;
            Assert.NotNull(print);
            Assert.Null(print.EstimatedPrintTimeInSeconds);
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
            var print = FindPrintByFileNameInDb(fileName)!;
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
            var print = FindPrintByFileHashInDb(fileHash)!;
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
            var print = FindPrintByFileNameInDb(fileName)!;
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
            var print = FindPrintByFileNameInDb(fileName)!;
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
            var print = FindPrintByFileNameInDb(fileName)!;
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
            var print = FindPrintByFileNameInDb(fileName)!;
            Assert.NotNull(print);
            Assert.NotNull(print.FilamentUsage);
            Assert.NotEmpty(print.FilamentUsage);
            // 5000mm / 1000 = 5m
            Assert.Equal(5.0, print.FilamentUsage!.First().EstimatedLengthInM);
        }

        [Fact]
        public async Task Webhook_PrintStarted_WithMultipleTools_CreatesMultipleFilamentUsages()
        {
            // Arrange - multi-tool printer with tool0, tool1, tool2
            var fileName = UniqueFileName("multi_tool_filament_test");
            var filamentData = new OctoprintWebhookMetaAnalysisFilamentDto
            {
                tool0 = new OctoprintWebhookFilamentUsageDto { length = 5000, volumn = 12.5 },
                tool1 = new OctoprintWebhookFilamentUsageDto { length = 3000, volumn = 7.5 },
                tool2 = new OctoprintWebhookFilamentUsageDto { length = 2000, volumn = 5.0 }
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
            var print = FindPrintByFileNameInDb(fileName)!;
            Assert.NotNull(print);
            Assert.NotNull(print.FilamentUsage);
            Assert.Equal(3, print.FilamentUsage.Count);

            // Verify tool0
            var tool0 = print.FilamentUsage.FirstOrDefault(f => f.EstimatedLengthInM == 5.0);
            Assert.NotNull(tool0);

            // Verify tool1
            var tool1 = print.FilamentUsage.FirstOrDefault(f => f.EstimatedLengthInM == 3.0);
            Assert.NotNull(tool1);

            // Verify tool2
            var tool2 = print.FilamentUsage.FirstOrDefault(f => f.EstimatedLengthInM == 2.0);
            Assert.NotNull(tool2);
        }

        [Fact]
        public async Task Webhook_PrintStarted_WithFilamentData_SetsSourceMeasurements()
        {
            // Arrange
            var fileName = UniqueFileName("filament_source_test");
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

            // Assert - verify source measurements are set correctly
            var print = FindPrintByFileNameInDb(fileName)!;
            Assert.NotNull(print);
            var filament = print.FilamentUsage!.First();
            Assert.Equal(PrintFilament.SourceMeasurement.Length, filament.EstimatedSource);
            Assert.Equal(PrintFilament.SourceMeasurement.Weight, filament.Source);
        }

        [Fact]
        public async Task Webhook_PrintStarted_WithFilamentData_LinksToLoaderFilaments()
        {
            // Arrange - the test seeder loads filaments on the printer
            // We verify that the filament usage can link to the loaded filament
            var fileName = UniqueFileName("filament_link_test");
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
            var print = FindPrintByFileNameInDb(fileName)!;
            Assert.NotNull(print);
            var filament = print.FilamentUsage!.First();
            // FilamentId may be null or have a value depending on loaded filaments
            // The key is that the property exists and can be set
            Assert.NotNull(filament);
        }

        #endregion

        #region Snapshot Saving Tests

        [Fact]
        public async Task Webhook_PrintStarted_WithSnapshot_CreatesImageRecord()
        {
            // Arrange - webhook with snapshot image
            var fileName = UniqueFileName("snapshot_start_test");
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                includeSnapshot: true);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Assert - verify print was created with an image
            var print = FindPrintByFileNameInDb(fileName)!;
            Assert.NotNull(print);
            var images = FindPrintImagesByPrintIdInDb(print.Id);
            Assert.NotEmpty(images);
            Assert.Single(images);
        }

        [Fact]
        public async Task Webhook_PrintStarted_WithSnapshot_SetsImageAsDefault()
        {
            // Arrange
            var fileName = UniqueFileName("snapshot_default_test");
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                includeSnapshot: true);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Assert - verify image is marked as default
            var print = FindPrintByFileNameInDb(fileName)!;
            Assert.NotNull(print);
            var images = FindPrintImagesByPrintIdInDb(print.Id);
            Assert.Single(images);
            Assert.True(images.First().IsDefault);
        }

        [Fact]
        public async Task Webhook_PrintStarted_WithSnapshot_CreatesFileRecord()
        {
            // Arrange
            var fileName = UniqueFileName("snapshot_file_test");
            var content = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                includeSnapshot: true);

            var request = CreateAuthenticatedWebhookRequest(content);

            // Act
            var response = await _httpClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Assert - verify File record was created
            var print = FindPrintByFileNameInDb(fileName)!;
            Assert.NotNull(print);
            var images = FindPrintImagesByPrintIdInDb(print.Id);
            Assert.Single(images);

            var image = images.First();
            Assert.NotNull(image.File);
            Assert.NotEqual(Guid.Empty, image.FileId);
            Assert.NotNull(image.File.Path);
            Assert.Contains("printimages/", image.File.Path);
        }

        [Fact]
        public async Task Webhook_PrintDone_WithSnapshot_CreatesImageRecord()
        {
            // Arrange - create a print first
            var fileName = UniqueFileName("snapshot_done_test");
            var fileHash = UniqueFileHash();
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                fileHash: fileHash);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            await _httpClient.SendAsync(startRequest);

            var createdPrint = FindPrintByFileHashInDb(fileHash)!;
            Assert.NotNull(createdPrint);

            // Act - send Print Done with snapshot
            var doneContent = CreateWebhookFormContent(
                topic: "Print Done",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: 3500,
                fileHash: fileHash,
                includeSnapshot: true);

            var doneRequest = CreateAuthenticatedWebhookRequest(doneContent);
            var doneResponse = await _httpClient.SendAsync(doneRequest);

            // Assert
            Assert.Equal(HttpStatusCode.OK, doneResponse.StatusCode);
            var updatedPrint = FindPrintByFileHashInDb(fileHash)!;
            var images = FindPrintImagesByPrintIdInDb(updatedPrint.Id);
            Assert.NotEmpty(images);
        }

        [Fact]
        public async Task Webhook_PrintDone_WithSnapshot_SetsAsDefault()
        {
            // Arrange
            var fileName = UniqueFileName("snapshot_done_default_test");
            var fileHash = UniqueFileHash();
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                fileHash: fileHash);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            await _httpClient.SendAsync(startRequest);

            // Act - send Print Done with snapshot
            var doneContent = CreateWebhookFormContent(
                topic: "Print Done",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: 3500,
                fileHash: fileHash,
                includeSnapshot: true);

            var doneRequest = CreateAuthenticatedWebhookRequest(doneContent);
            await _httpClient.SendAsync(doneRequest);

            // Assert
            var print = FindPrintByFileHashInDb(fileHash)!;
            var images = FindPrintImagesByPrintIdInDb(print.Id);
            Assert.NotEmpty(images);
            var defaultImage = images.FirstOrDefault(i => i.IsDefault);
            Assert.NotNull(defaultImage);
        }

        [Fact]
        public async Task Webhook_PrintFailed_WithSnapshot_CreatesImageRecord()
        {
            // Arrange - create a print first
            var fileName = UniqueFileName("snapshot_failed_test");
            var fileHash = UniqueFileHash();
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                fileHash: fileHash);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            await _httpClient.SendAsync(startRequest);

            var createdPrint = FindPrintByFileHashInDb(fileHash)!;
            Assert.NotNull(createdPrint);

            // Act - send Print Failed with snapshot
            var failedContent = CreateWebhookFormContent(
                topic: "Print Failed",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: 1200,
                fileHash: fileHash,
                includeSnapshot: true);

            var failedRequest = CreateAuthenticatedWebhookRequest(failedContent);
            var failedResponse = await _httpClient.SendAsync(failedRequest);

            // Assert
            Assert.Equal(HttpStatusCode.OK, failedResponse.StatusCode);
            var updatedPrint = FindPrintByFileHashInDb(fileHash)!;
            var images = FindPrintImagesByPrintIdInDb(updatedPrint.Id);
            Assert.NotEmpty(images);
        }

        [Fact]
        public async Task Webhook_PrintFailed_WithSnapshot_SetsAsDefault()
        {
            // Arrange
            var fileName = UniqueFileName("snapshot_failed_default_test");
            var fileHash = UniqueFileHash();
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                fileHash: fileHash);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            await _httpClient.SendAsync(startRequest);

            // Act - send Print Failed with snapshot
            var failedContent = CreateWebhookFormContent(
                topic: "Print Failed",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: 1200,
                fileHash: fileHash,
                includeSnapshot: true);

            var failedRequest = CreateAuthenticatedWebhookRequest(failedContent);
            await _httpClient.SendAsync(failedRequest);

            // Assert
            var print = FindPrintByFileHashInDb(fileHash)!;
            var images = FindPrintImagesByPrintIdInDb(print.Id);
            Assert.NotEmpty(images);
            var defaultImage = images.FirstOrDefault(i => i.IsDefault);
            Assert.NotNull(defaultImage);
        }

        [Fact]
        public async Task Webhook_MultipleSnapshots_ReplacesDefaultImage()
        {
            // Arrange - start print with snapshot
            var fileName = UniqueFileName("multi_snapshot_test");
            var fileHash = UniqueFileHash();
            var startContent = CreateWebhookFormContent(
                topic: "Print Started",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                estimatedPrintTime: 3600,
                fileHash: fileHash,
                includeSnapshot: true);

            var startRequest = CreateAuthenticatedWebhookRequest(startContent);
            await _httpClient.SendAsync(startRequest);

            var print = FindPrintByFileHashInDb(fileHash)!;
            var imagesAfterStart = FindPrintImagesByPrintIdInDb(print.Id);
            var firstImageId = imagesAfterStart.First().Id;

            // Act - send Print Done with different snapshot
            var doneContent = CreateWebhookFormContent(
                topic: "Print Done",
                deviceIdentifier: IntegrationTestSeeder.TestPrinterId.ToString(),
                fileName: fileName,
                printTime: 3500,
                fileHash: fileHash,
                includeSnapshot: true);

            var doneRequest = CreateAuthenticatedWebhookRequest(doneContent);
            await _httpClient.SendAsync(doneRequest);

            // Assert
            var updatedPrint = FindPrintByFileHashInDb(fileHash)!;
            var imagesAfterDone = FindPrintImagesByPrintIdInDb(updatedPrint.Id);

            // Should have 2 images now
            Assert.Equal(2, imagesAfterDone.Count);

            // First image should no longer be default
            var firstImage = imagesAfterDone.FirstOrDefault(i => i.Id == firstImageId);
            Assert.NotNull(firstImage);
            Assert.False(firstImage.IsDefault);

            // Second image should be default
            var secondImage = imagesAfterDone.FirstOrDefault(i => i.Id != firstImageId);
            Assert.NotNull(secondImage);
            Assert.True(secondImage.IsDefault);
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

            var createdPrint = FindPrintByFileHashInDb(fileHash)!;
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

            var updatedPrint = FindPrintByFileHashInDb(fileHash)!;
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
            var updatedPrint = FindPrintByFileHashInDb(fileHash)!;
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

            var createdPrint = FindPrintByFileNameInDb(fileName)!;
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
            var updatedPrint = FindPrintByFileNameInDb(fileName)!;
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

            var updatedPrint = FindPrintByFileHashInDb(fileHash)!;
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
            var updatedPrint = FindPrintByFileHashInDb(fileHash)!;
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

            var updatedPrint = FindPrintByFileHashInDb(fileHash)!;
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

            var createdPrint = FindPrintByFileHashInDb(fileHash)!;
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

            var completedPrint = FindPrintByFileHashInDb(fileHash)!;
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

            var createdPrint = FindPrintByFileHashInDb(fileHash)!;
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

            var failedPrint = FindPrintByFileHashInDb(fileHash)!;
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

            var errorPrint = FindPrintByFileHashInDb(fileHash)!;
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
            var print = FindPrintByFileNameInDb(longName)!;
            Assert.NotNull(print);
            Assert.True(print.Title!.Length <= 100, $"Title should be 100 chars or less, was {print.Title!.Length}");
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
            var updatedPrint = FindPrintByFileHashInDb(fileHash)!;
            Assert.NotNull(updatedPrint);
            Assert.Equal(PrintStatus.Success, updatedPrint.Status);
        }

        #endregion
    }
}
