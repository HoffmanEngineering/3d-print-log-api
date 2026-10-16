using Microsoft.ApplicationInsights.DataContracts;
using PrintLogApi.Telemetry;
using Xunit;

namespace PrintLogApi.IntegrationTests.Telemetry;

public class TagUidRedactionTests
{
    private static readonly TagUidRedactionInitializer Initializer = new();

    [Fact]
    public void ByTagRequest_UidRemoved()
    {
        var request = new RequestTelemetry
        {
            Url = new Uri("https://api.3dprintlog.com/api/Filaments/by-tag/04A23F11223380?x=1"),
            Name = "GET /api/Filaments/by-tag/04A23F11223380",
        };

        Initializer.Initialize(request);

        Assert.Equal("https://api.3dprintlog.com/api/Filaments/by-tag/REDACTED?x=1", request.Url.ToString());
        Assert.DoesNotContain("04A23F11223380", request.Name);
    }

    [Fact]
    public void RouteTemplateName_Untouched()
    {
        var request = new RequestTelemetry
        {
            Url = new Uri("https://api.3dprintlog.com/api/Filaments/by-tag/04A23F11"),
            Name = "GET Filaments/GetByTag [uid]",
        };

        Initializer.Initialize(request);

        Assert.Equal("GET Filaments/GetByTag [uid]", request.Name);
        Assert.DoesNotContain("04A23F11", request.Url.ToString());
    }

    [Fact]
    public void OtherPaths_Untouched()
    {
        var request = new RequestTelemetry { Url = new Uri("https://api.3dprintlog.com/api/Filaments/abc"), Name = "GET /api/Filaments/abc" };

        Initializer.Initialize(request);

        Assert.Equal("https://api.3dprintlog.com/api/Filaments/abc", request.Url.ToString());
        Assert.Equal("GET /api/Filaments/abc", request.Name);
    }
}
