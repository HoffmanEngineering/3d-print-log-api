using System.Net;
using System.Text.Json;
using PrintLogApi.Models;
using PrintLogApi.Webhooks;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Webhooks;

/// <summary>
/// A usage row that names another user's spool (written before the ownership checks) must not
/// carry that spool's name, brand, material or color to the print owner's webhook. The row's
/// spool id, slot and amounts stay, as get_print reports them.
/// </summary>
public class WebhookForeignSpoolTests(WebhookTestFactory factory) : IClassFixture<WebhookTestFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Filament Spool(User owner, string name, string brand) => new()
    {
        Id = Guid.NewGuid(),
        Brand = brand,
        DisplayName = name,
        MaterialType = "PETG-CF",
        ColorHex = "#123456",
        ColorName = "x",
        MaterialCategoryNickname = "filament",
        MaterialDensityGramPerCubicCm = 1.27,
        DiameterMm = 1.75,
        InitialNominalWeightMg = 1_000_000,
        Source = Filament.SourceMeasurement.Weight,
        IsActive = true,
        CreatedById = owner.Id,
        UpdatedById = owner.Id,
    };

    [Fact]
    public async Task PrintFinished_Payload_LeavesOutAnotherUsersSpoolDetails_AndKeepsOwn()
    {
        factory.DiscardPending();
        var owner = await factory.CreateUserAsync();
        var other = await factory.CreateUserAsync();
        var printerId = await factory.CreatePrinterAsync(owner, "Owner printer");
        var host = $"foreign-spool-{Guid.NewGuid():N}.example.test";
        var subscribe = new HttpRequestMessage(HttpMethod.Post, "/api/Webhooks") { Content = JsonContent.Create(new { url = $"https://{host}/hook" }) };
        subscribe.Headers.Add(TestAuthHandler.TestUserIdHeader, owner.OAuthUserId);
        Assert.Equal(HttpStatusCode.Created, (await _client.SendAsync(subscribe, Ct)).StatusCode);

        var foreign = Spool(other, "Other user's spool", "Other-Brand");
        var own = Spool(owner, "Owner's spool", "Owner-Brand");
        long printId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.Filaments.AddRange(foreign, own);
            var print = new Print
            {
                Title = "Owner print",
                PrinterId = printerId,
                Status = PrintStatus.Success,
                ViewStatus = PrintViewStatus.Private,
                StartDate = DateTimeOffset.UtcNow.AddHours(-1),
                PrintTimeInSeconds = 3600,
                CreatedById = owner.Id,
                UpdatedById = owner.Id,
                FilamentUsage =
                [
                    new PrintFilament { FilamentId = foreign.Id, AmountMg = 5_000, Slot = 0, Source = PrintFilament.SourceMeasurement.Weight, EstimatedSource = PrintFilament.SourceMeasurement.Weight },
                    new PrintFilament { FilamentId = own.Id, AmountMg = 7_000, Slot = 1, Source = PrintFilament.SourceMeasurement.Weight, EstimatedSource = PrintFilament.SourceMeasurement.Weight },
                ],
            };
            db.Prints.Add(print);
            await db.SaveChangesAsync(Ct);
            printId = print.Id;
        }

        var queue = factory.Services.GetRequiredService<WebhookEventQueue>();
        Assert.True(queue.TryWrite(new WebhookEvent($"evt_{Guid.NewGuid():N}", WebhookEventTypes.PrintFinished, owner.Id, printId, DateTimeOffset.UtcNow)));
        await factory.DeliverPendingAsync(Ct);

        var received = Assert.Single(factory.Receiver.Received(host));
        var rows = JsonDocument.Parse(received.Body).RootElement.GetProperty("data").GetProperty("filament").EnumerateArray().ToList();
        var foreignRow = rows.Single(r => r.GetProperty("filamentId").GetGuid() == foreign.Id);
        var ownRow = rows.Single(r => r.GetProperty("filamentId").GetGuid() == own.Id);

        foreach (var field in new[] { "name", "brand", "materialType", "colorHex" })
        {
            Assert.True(!foreignRow.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null, $"{field}: {foreignRow}");
        }
        Assert.Equal(0, foreignRow.GetProperty("slot").GetInt32());
        Assert.Equal(5, foreignRow.GetProperty("grams").GetDouble());

        Assert.Equal("Owner's spool", ownRow.GetProperty("name").GetString());
        Assert.Equal("Owner-Brand", ownRow.GetProperty("brand").GetString());
        Assert.Equal("PETG-CF", ownRow.GetProperty("materialType").GetString());
        Assert.Equal("#123456", ownRow.GetProperty("colorHex").GetString());
    }
}
