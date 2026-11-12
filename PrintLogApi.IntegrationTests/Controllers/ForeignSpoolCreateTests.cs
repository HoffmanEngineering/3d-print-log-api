using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Authentication.Devices;
using PrintLogApi.Models.DTOs.Printer;
using PrintLogApi.Services;
using Xunit;
using static PrintLogApi.IntegrationTests.Devices.DeviceTestKit;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// Creating a printer: another user's spool is refused on every credential, as PUT refuses it.
/// </summary>
public class ForeignSpoolCreateTests(CustomWebApplicationFactory factory)
    : ForeignSpoolTestBase(factory), IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task PostPrinter_WithAnotherUsersSpool_IsRefusedLikePut_AndCreatesNothing()
    {
        var (victim, other) = await SeedPairAsync();
        var printersBefore = await PrinterCountAsync(other.User.Id);

        var post = await SendAsync(As(PrinterBody(HttpMethod.Post, "/api/Printers", victim.Filament.Id), other.User));
        var put = await SendAsync(As(PrinterBody(HttpMethod.Put, $"/api/Printers/{other.Printer.Id}", victim.Filament.Id, other.Printer.Id), other.User));

        Assert.Equal(HttpStatusCode.Forbidden, put.Status);
        Assert.Equal(put.Status, post.Status);
        Assert.Equal(put.Body, post.Body);
        Assert.Equal(printersBefore, await PrinterCountAsync(other.User.Id));
        Assert.All(await LoadedRowsForFilamentAsync(victim.Filament.Id), row => Assert.Equal(victim.Printer.Id, row.PrinterId));
    }

    [Fact]
    public async Task PostPrinter_WithAnotherUsersSpool_ApiKey_IsRefused()
    {
        var (victim, other) = await SeedPairAsync();
        string key;
        using (var scope = _factory.Services.CreateScope())
        {
            key = (await scope.ServiceProvider.GetRequiredService<IUserApiKeyService>().GenerateNewApiKey(other.User.Id, "foreign-spool")).PublicKey!;
        }
        var request = PrinterBody(HttpMethod.Post, "/api/Printers", victim.Filament.Id);
        request.Headers.Add("X-Api-Key", key);

        var (status, _) = await SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal(1, await PrinterCountAsync(other.User.Id));
        Assert.All(await LoadedRowsForFilamentAsync(victim.Filament.Id), row => Assert.Equal(victim.Printer.Id, row.PrinterId));
    }

    [Fact]
    public async Task PostPrinter_WithAnotherUsersSpool_DeviceTokenWithPrintersCreate_IsRefused()
    {
        var (victim, _) = await SeedPairAsync();
        var token = await TokenAsync(_client, NewSubject("foreign-spool"), [.. BridgeScopes, DeviceScopes.PrintersCreate]);

        var (status, _) = await SendAsync(WithDevice(PrinterBody(HttpMethod.Post, "/api/Printers", victim.Filament.Id), token));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.All(await LoadedRowsForFilamentAsync(victim.Filament.Id), row => Assert.Equal(victim.Printer.Id, row.PrinterId));
    }

    [Fact]
    public async Task PostPrinter_WithOwnSpool_LoadsIt_AndWithNoSpools_Creates()
    {
        var owner = await SeedOwnerAsync(loadOwnSpool: false);

        var (status, body) = await SendAsync(As(PrinterBody(HttpMethod.Post, "/api/Printers", owner.Filament.Id), owner.User));
        Assert.True(status == HttpStatusCode.Created, $"{(int)status} {body}");
        var printerId = JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt64();
        var detail = await GetJsonAsync($"/api/Printers/{printerId}", owner.User);
        var loaded = Assert.Single(detail.GetProperty("loadedFilaments").EnumerateArray());
        Assert.Equal(owner.Filament.Id, loaded.GetProperty("filament").GetProperty("id").GetGuid());
        Assert.Equal(InitialMg, loaded.GetProperty("filament").GetProperty("filamentRemaining").GetInt64());

        var onboarding = new HttpRequestMessage(HttpMethod.Post, "/api/Printers")
        {
            Content = JsonContent.Create(new AddPrinterDTO { Name = "Onboarding", Make = "x", Model = "y", IsActive = true, LoadedFilaments = [] }),
        };
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(As(onboarding, owner.User))).Status);
    }
}
