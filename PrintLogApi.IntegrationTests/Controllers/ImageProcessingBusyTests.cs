using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PrintLogApi.Middleware;
using PrintLogApi.Services;
using SkiaSharp;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// An upload that cannot get image memory in time is answered 503 with a <c>Retry-After</c> and a
/// code, never a 500, and goes through once the budget frees up.
/// </summary>
public class ImageProcessingBusyTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestMessage Upload(string path, string field)
    {
        using var bitmap = new SKBitmap(400, 300);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var content = new ByteArrayContent(data.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new MultipartFormDataContent { { content, field, "photo.png" } },
        };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        return request;
    }

    [Fact]
    public async Task APrintImage_WhileTheBudgetIsSpent_Is503WithRetryAfter_ThenSucceeds()
    {
        await using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ImageProcessing:MemoryBudgetMb"] = "64",
                ["ImageProcessing:MaxWaitSeconds"] = "0.2",
            })));
        using var client = host.CreateClient();
        var gate = host.Services.GetRequiredService<ImageMemoryGate>();
        Assert.Equal(64L * 1024 * 1024, gate.BudgetBytes);
        var path = $"/api/Prints/{IntegrationTestSeeder.TestPrintId}/image";

        var held = await gate.AcquireAsync(gate.BudgetBytes, Ct);
        var busy = await client.SendAsync(Upload(path, "image"), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(ImageProcessingBusyFilter.RetryAfterSeconds), busy.Headers.RetryAfter!.Delta);
        Assert.Equal("application/problem+json", busy.Content.Headers.ContentType!.MediaType);
        var body = await busy.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(ImageProcessingBusyFilter.Code, body.GetProperty("code").GetString());

        held.Dispose();
        var retried = await client.SendAsync(Upload(path, "image"), Ct);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.Equal(gate.BudgetBytes, gate.AvailableBytes);
    }

    /// <summary>
    /// The test server cannot enforce a body limit (it has no IHttpMaxRequestBodySizeFeature), so
    /// this pins the attribute Kestrel enforces: the 10 MB image limit plus 1 MB of multipart
    /// framing, refused before the form is buffered rather than after.
    /// </summary>
    [Theory]
    [InlineData(typeof(PrintLogApi.Controllers.PrintsController), nameof(PrintLogApi.Controllers.PrintsController.PostImage))]
    [InlineData(typeof(PrintLogApi.Controllers.ProjectsController), nameof(PrintLogApi.Controllers.ProjectsController.PostProjectImage))]
    public void PrintAndProjectImageUploads_CapTheRequestBody(Type controller, string action)
    {
        var method = controller.GetMethod(action);
        Assert.NotNull(method);
        var limit = Assert.Single(method!.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), false));
        Assert.Equal(11L * 1024 * 1024, ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)limit).MaxRequestBodySize);
    }
}
