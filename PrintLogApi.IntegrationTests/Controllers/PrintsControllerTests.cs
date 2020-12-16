using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers
{
    public class PrintsControllerTests : IClassFixture<WebApplicationFactory<Startup>>
    {
        private readonly HttpClient _httpClient;

        public PrintsControllerTests(WebApplicationFactory<Startup> factory)
        {

            _httpClient = factory.CreateDefaultClient();
        }

        [Fact]
        public async Task GetPrintSummary_ReturnsExpectedJson()
        {
            var model = await _httpClient.GetFromJsonAsync<PagedList<PrintSummaryDTO>>("/api/Prints/summary");
            
            Assert.NotNull(model);
            Assert.Equal(1, model.Paging.CurrentPage);
        }

    }
}
