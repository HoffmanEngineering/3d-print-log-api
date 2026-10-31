using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Authentication.Devices;
using Xunit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Devices;

/// <summary>
/// A device token may edit a print it logged, but never who can see it or its files: a copied
/// bridge token must not be able to publish a user's private prints with their G-code downloadable.
/// </summary>
public class DevicePrintPatchTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> Send(HttpRequestMessage request) => _client.SendAsync(request, TestContext.Current.CancellationToken);

    private static HttpRequestMessage Patch(long printId, object body) =>
        new(HttpMethod.Patch, $"/api/Prints/{printId}") { Content = JsonContent.Create(body) };

    private async Task<(string Subject, string Token, long PrintId)> PrivatePrint(string label)
    {
        var subject = NewSubject(label);
        var token = await TokenAsync(_client, subject, DeviceScopes.All);
        var created = await Send(PrintCreate(token, await CreatePrinterAsync(_client, subject)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var printId = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetInt64();

        var makePrivate = await Send(AsUser(Patch(printId, new { viewStatus = (int)PrintViewStatus.Private, allowFileDownloads = false, allowComments = false }), subject));
        Assert.Equal(HttpStatusCode.OK, makePrivate.StatusCode);
        return (subject, token, printId);
    }

    private async Task<(PrintViewStatus ViewStatus, bool AllowFileDownloads, bool AllowComments)> Sharing(long printId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var print = await db.Prints.AsNoTracking().SingleAsync(p => p.Id == printId, TestContext.Current.CancellationToken);
        return (print.ViewStatus, print.AllowFileDownloads, print.AllowComments);
    }

    public static TheoryData<string> SharingFields => new()
    {
        """{"viewStatus":1}""",
        """{"allowFileDownloads":true}""",
        """{"allowComments":true}""",
        """{"notes":"done","viewStatus":2}""",
    };

    [Theory]
    [MemberData(nameof(SharingFields))]
    public async Task ADevice_CannotChangeWhoCanSeeAPrint(string body)
    {
        var (_, token, printId) = await PrivatePrint("sharing");

        var request = WithDevice(new HttpRequestMessage(HttpMethod.Patch, $"/api/Prints/{printId}")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        }, token);
        var response = await Send(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(DeviceTokenErrors.FieldNotAllowed, await CodeOf(response));
        Assert.Equal((PrintViewStatus.Private, false, false), await Sharing(printId));
    }

    [Fact]
    public async Task ADevice_StillPatchesNotes_AsTheBridgeDoes()
    {
        var (_, token, printId) = await PrivatePrint("notes");

        var response = await Send(WithDevice(Patch(printId, new { notes = "Finished: complete" }), token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Finished: complete", body.GetProperty("notes").GetString());
        Assert.Equal((PrintViewStatus.Private, false, false), await Sharing(printId));
    }

    [Fact]
    public async Task TheOwner_StillChangesSharing()
    {
        var (subject, _, printId) = await PrivatePrint("owner");

        var response = await Send(AsUser(Patch(printId, new { viewStatus = (int)PrintViewStatus.Public, allowFileDownloads = true, allowComments = true }), subject));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((PrintViewStatus.Public, true, true), await Sharing(printId));
    }
}
