using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace PrintLogApi.IntegrationTests
{

    public class MetricsTests : IClassFixture<WebApplicationFactory<Startup>>
    {
        private readonly HttpClient _httpClient;

        public MetricsTests(WebApplicationFactory<Startup> factory)
        {
            
            _httpClient = factory.CreateDefaultClient();
        }

        [Fact]
        public async Task Metrics_ReturnsOk()
        {
            var response = await _httpClient.GetAsync("/metrics");

            response.EnsureSuccessStatusCode();
        }
    }
}
