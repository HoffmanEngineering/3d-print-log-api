using Microsoft.ApplicationInsights.DataContracts;
using PrintLogApi.Telemetry;
using Xunit;

namespace PrintLogApi.IntegrationTests.Telemetry;

public class ApiKeyRedactionTests
{
    private static readonly ApiKeyRedactionInitializer Initializer = new();

    [Theory]
    [InlineData("https://api.3dprintlog.com/api/Prints?api_key=SECRET&page=2")]
    [InlineData("https://api.3dprintlog.com/api/Prints?page=2&api_key=SECRET")]
    // ASP.NET Core matches query keys case-insensitively and after decoding, so both of these
    // authenticate too.
    [InlineData("https://api.3dprintlog.com/api/Prints?page=2&API_KEY=SECRET")]
    [InlineData("https://api.3dprintlog.com/api/Prints?page=2&api%5Fkey=SECRET")]
    [InlineData("https://api.3dprintlog.com/api/Prints?page=2&%61pi_key=SECRET")]
    [InlineData("https://api.3dprintlog.com/api/Prints?api_key=SECRET&page=2&api_key=SECRET")]
    public void RequestUrl_KeyRemoved_OtherParamsKept(string url)
    {
        var request = new RequestTelemetry { Url = new Uri(url), Name = "GET " + url };

        Initializer.Initialize(request);

        Assert.DoesNotContain("SECRET", request.Url.AbsoluteUri);
        Assert.Contains("page=2", request.Url.Query);
        Assert.DoesNotContain("SECRET", request.Name);
    }

    // A rejected key is logged as a 401 request; it is still somebody's credential (or a typo of one).
    [Fact]
    public void FailedRequest_KeyRemoved()
    {
        var request = new RequestTelemetry
        {
            Url = new Uri("https://api.3dprintlog.com/api/Printers/summary?api_key=SECRET"),
            Name = "GET /api/Printers/summary",
            ResponseCode = "401",
            Success = false,
        };

        Initializer.Initialize(request);

        Assert.DoesNotContain("SECRET", request.Url.AbsoluteUri);
    }

    [Fact]
    public void Dependency_KeyRemoved()
    {
        var dependency = new DependencyTelemetry { Data = "https://api.3dprintlog.com/api/Prints?a=1&api_key=SECRET" };

        Initializer.Initialize(dependency);

        Assert.DoesNotContain("SECRET", dependency.Data);
        Assert.Contains("a=1", dependency.Data);
    }

    [Fact]
    public void UrlWithoutKey_Untouched()
    {
        var request = new RequestTelemetry
        {
            Url = new Uri("https://api.3dprintlog.com/api/prints?my_api_key_name=keep&q=a%20b"),
            Name = "GET /api/prints",
        };

        Initializer.Initialize(request);

        Assert.Equal("https://api.3dprintlog.com/api/prints?my_api_key_name=keep&q=a%20b", request.Url.AbsoluteUri);
    }

    // Proves the initializer is wired into the host's telemetry pipeline, not just that the class
    // works in isolation. (Asserting on a real request item is unreliable: the SDK's request
    // tracking hooks a process-wide listener; see EmailControllerTests.)
    [Fact]
    public async Task Host_RedactsKeyedRequestUrl()
    {
        await using var factory = new CustomWebApplicationFactory();
        var configuration = factory.Services.GetRequiredService<Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration>();
        var request = new RequestTelemetry
        {
            Url = new Uri("https://api.3dprintlog.test/api/Printers/summary?api_key=SECRET"),
            Name = "GET Printers/GetPrinterSummary",
        };

        foreach (var initializer in configuration.TelemetryInitializers)
        {
            initializer.Initialize(request);
        }

        Assert.DoesNotContain("SECRET", request.Url.AbsoluteUri);
        Assert.Equal("GET Printers/GetPrinterSummary", request.Name);
    }
}
