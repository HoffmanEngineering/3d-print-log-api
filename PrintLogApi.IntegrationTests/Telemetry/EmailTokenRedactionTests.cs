using Microsoft.ApplicationInsights.DataContracts;
using PrintLogApi.Telemetry;
using Xunit;

namespace PrintLogApi.IntegrationTests.Telemetry;

public class EmailTokenRedactionTests
{
    private static readonly EmailTokenRedactionInitializer Initializer = new();

    [Fact]
    public void RequestUrl_TokenRemoved_OtherParamsKept()
    {
        var request = new RequestTelemetry
        {
            Url = new Uri("https://api.3dprintlog.com/api/email/unsubscribe?t=SECRET&x=1"),
            Name = "POST /api/email/unsubscribe?t=SECRET&x=1",
        };

        Initializer.Initialize(request);

        Assert.DoesNotContain("SECRET", request.Url.ToString());
        Assert.Contains("x=1", request.Url.Query);
        Assert.DoesNotContain("SECRET", request.Name);
    }

    [Fact]
    public void Dependency_TokenRemoved()
    {
        var dependency = new DependencyTelemetry { Data = "https://api.3dprintlog.com/api/email/unsubscribe?a=1&t=SECRET" };

        Initializer.Initialize(dependency);

        Assert.DoesNotContain("SECRET", dependency.Data);
        Assert.Contains("a=1", dependency.Data);
    }

    // Only the email routes carry tokens in t; other endpoints' query strings are left alone.
    [Fact]
    public void OtherPaths_Untouched()
    {
        var request = new RequestTelemetry { Url = new Uri("https://api.3dprintlog.com/api/prints?t=keep"), Name = "GET /api/prints" };

        Initializer.Initialize(request);

        Assert.Equal("https://api.3dprintlog.com/api/prints?t=keep", request.Url.ToString());
    }
}
