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

            // Two prints in the project, three usage rows between them, spanning two filaments -
            // and one row with no filament id at all. The projection groups by project and sums
            // per filament, so this is the smallest fixture that pins the grouping rather than
            // just the happy path: a restructure that merged the two filaments, dropped one, or
            // let the id-less row through would all show up here.
            db.Prints.AddRange(
                new Print
                {
                    Title = "Grouped Feed Print A",
                    StartDate = DateTimeOffset.UtcNow.AddDays(-2),
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
                },
                new Print
                {
                    Title = "Grouped Feed Print B",
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
                        new() { FilamentId = IntegrationTestSeeder.TestFilamentId2, Source = PrintFilament.SourceMeasurement.Weight, AmountMg = 7000 },
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
        public async Task GroupedFeed_ProjectUsage_GroupsBothFilamentsUnderTheProject()
        {
            var item = await GetProjectItem();

            // Exactly two: the two filaments, aggregated across both prints. The third usage row
            // has no filament id and must not appear - the projection requires BOTH ids.
            var usage = item.FilamentUsage!.ToList();
            Assert.Equal(2, usage.Count);
            Assert.Equal(
                new[] { IntegrationTestSeeder.TestFilamentId1, IntegrationTestSeeder.TestFilamentId2 }.OrderBy(g => g),
                usage.Select(u => u.Id).OrderBy(g => g));
        }

        [Fact]
        public async Task GroupedFeed_ProjectUsage_KeepsEachFilamentsAmountSeparate()
        {
            var item = await GetProjectItem();

            // A restructure that grouped on the wrong key would merge these into one row, or
            // attach the wrong total to each id.
            var usage = item.FilamentUsage!.ToList();
            Assert.Equal(25000, usage.Single(u => u.Id == IntegrationTestSeeder.TestFilamentId1).AmountMg);
            Assert.Equal(7000, usage.Single(u => u.Id == IntegrationTestSeeder.TestFilamentId2).AmountMg);
        }

        [Fact]
        public async Task GroupedFeed_ProjectUsage_ResolvesEachFilamentEntity()
        {
            var item = await GetProjectItem();

            // The entity lookup is keyed on the same unwrapped id, so a projection that lost or
            // mismatched it would leave these null or cross-wired.
            foreach (var usage in item.FilamentUsage!)
            {
                Assert.NotNull(usage.Filament);
                Assert.Equal(usage.Id, usage.Filament!.Id);
            }
        }
    }
}
