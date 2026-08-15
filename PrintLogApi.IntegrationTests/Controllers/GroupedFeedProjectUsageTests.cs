using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers
{
    /// <summary>
    /// Covers the project-filament-usage projection in PrintService.GetGroupedFeedAsync.
    ///
    /// This exists because that projection had no coverage at all: the mainline seeder creates no
    /// projects, so the whole branch was dead in every existing test and a rewrite of it could not
    /// have been caught. Its own class so the extra project and print stay out of the seeded counts
    /// other test classes assert on - each factory instance gets its own in-memory database.
    /// </summary>
    public class GroupedFeedProjectUsageTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;
        private readonly HttpClient _httpClient;

        private static readonly Guid ProjectId = new Guid("bbbbbbbb-0001-0000-0000-000000000000");

        public GroupedFeedProjectUsageTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
            _httpClient = factory.CreateClient();
            SeedProjectWithUsage();
        }

        private void SeedProjectWithUsage()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            if (db.Projects.Any(p => p.Id == ProjectId)) return;

            var now = DateTime.UtcNow;
            db.Projects.Add(new Project
            {
                Id = ProjectId,
                Name = "Grouped Feed Project",
                Status = Project.ProjectStatus.InProgress,
                ViewStatus = Project.ProjectViewStatus.Public,
                CreatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = now,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                UpdatedDate = now,
            });

            // Two usage rows on one print: one carries a filament id, one does not. Only the
            // first may reach the lookup - the projection requires BOTH ProjectId and FilamentId.
            db.Prints.Add(new Print
            {
                Title = "Grouped Feed Print",
                StartDate = DateTimeOffset.UtcNow.AddDays(-1),
                Status = Print.PrintStatus.Success,
                ViewStatus = Print.PrintViewStatus.Public,
                PrinterId = IntegrationTestSeeder.TestPrinterId,
                ProjectId = ProjectId,
                CreatedById = IntegrationTestSeeder.TestUserId,
                CreatedDate = now,
                UpdatedById = IntegrationTestSeeder.TestUserId,
                UpdatedDate = now,
                FilamentUsage = new List<PrintFilament>
                {
                    new() { FilamentId = IntegrationTestSeeder.TestFilamentId1, Source = PrintFilament.SourceMeasurement.Weight, AmountMg = 25000 },
                    new() { FilamentId = null, Source = PrintFilament.SourceMeasurement.Weight, AmountMg = 9000 },
                },
            });

            db.SaveChanges();
        }

        private async Task<GroupedFeedItemDto> GetProjectItem()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/Prints/grouped?pageSize=100");
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var page = (await response.Content.ReadFromJsonAsync<PagedList<GroupedFeedItemDto>>())!;

            return page.Items.Single(i => i.ProjectId == ProjectId);
        }

        [Fact]
        public async Task GroupedFeed_ProjectUsage_CarriesTheRowWithBothIds()
        {
            var item = await GetProjectItem();

            var usage = Assert.Single(item.FilamentUsage!);
            Assert.Equal(IntegrationTestSeeder.TestFilamentId1, usage.Id);
            Assert.Equal(25000, usage.AmountMg);
        }

        [Fact]
        public async Task GroupedFeed_ProjectUsage_ResolvesTheFilamentEntity()
        {
            var item = await GetProjectItem();

            // The lookup is keyed on the same unwrapped id, so a projection that lost or
            // mismatched it would leave this null.
            var usage = Assert.Single(item.FilamentUsage!);
            Assert.NotNull(usage.Filament);
            Assert.Equal(IntegrationTestSeeder.TestFilamentId1, usage.Filament!.Id);
        }
    }
}
