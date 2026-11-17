using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models.DTOs.Connection;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Printer;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// The connection registry (#149): which connector is attached to which printer, and whether it
/// is still alive.
/// </summary>
public class ConnectionsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _httpClient;

    public ConnectionsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    #region PUT (register and heartbeat)

    [Fact]
    public async Task Put_NewInstance_CreatesTheConnection()
    {
        var instanceId = NewInstanceId();
        var before = DateTimeOffset.UtcNow;

        var response = await PutAsync(instanceId, new PutConnectionDto
        {
            Kind = "moonraker",
            DisplayName = "Voron bridge",
            AgentVersion = "0.1.0",
            PrinterId = IntegrationTestSeeder.TestPrinterId,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var connection = await ReadAsync(response);
        Assert.NotEqual(Guid.Empty, connection.Id);
        Assert.Equal(instanceId, connection.InstanceId);
        Assert.Equal("moonraker", connection.Kind);
        Assert.Equal("Voron bridge", connection.DisplayName);
        Assert.Equal("0.1.0", connection.AgentVersion);
        Assert.Equal(IntegrationTestSeeder.TestPrinterId, connection.PrinterId);
        Assert.Equal(ConnectionStatus.Online, connection.Status);
        Assert.True(connection.LastSeenAt >= before.AddSeconds(-1), $"lastSeenAt {connection.LastSeenAt} is before the request");
    }

    [Fact]
    public async Task Put_SameInstanceAgain_UpdatesTheOneConnection()
    {
        var instanceId = NewInstanceId();
        var first = await ReadAsync(await PutAsync(instanceId, Body(displayName: "Old name", agentVersion: "0.1.0")));

        var response = await PutAsync(instanceId, Body(displayName: "New name", agentVersion: "0.2.0"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var second = await ReadAsync(response);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("New name", second.DisplayName);
        Assert.Equal("0.2.0", second.AgentVersion);
        Assert.True(second.LastSeenAt >= first.LastSeenAt);
        Assert.Single(await ListAsync(), c => c.InstanceId == instanceId);
    }

    [Fact]
    public async Task Put_HeartbeatAfterGoingStale_IsOnlineAgain()
    {
        var instanceId = NewInstanceId();
        await PutAsync(instanceId, Body());
        await BackdateLastSeenAsync(instanceId, TimeSpan.FromHours(1));

        var connection = await ReadAsync(await PutAsync(instanceId, Body()));

        Assert.Equal(ConnectionStatus.Online, connection.Status);
    }

    [Fact]
    public async Task Put_KindIsNormalizedToLowerCase()
    {
        var connection = await ReadAsync(await PutAsync(NewInstanceId(), Body(kind: "  MoonRaker ")));

        Assert.Equal("moonraker", connection.Kind);
    }

    [Fact]
    public async Task Put_OneBridgeWatchingThreePrinters_RegistersThreeConnections()
    {
        var printers = new[]
        {
            await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId),
            await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId),
            await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId),
        };
        var bridge = Guid.NewGuid().ToString("N");

        foreach (var printerId in printers)
        {
            var response = await PutAsync($"{bridge}-{printerId}", Body(printerId: printerId));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        foreach (var printerId in printers)
        {
            var onPrinter = await ListAsync(printerId);
            var connection = Assert.Single(onPrinter);
            Assert.Equal($"{bridge}-{printerId}", connection.InstanceId);
        }
    }

    [Fact]
    public async Task Put_WithoutAPrinter_RegistersAnUnboundConnection()
    {
        var connection = await ReadAsync(await PutAsync(NewInstanceId(), Body(printerId: null)));

        Assert.Null(connection.PrinterId);
    }

    [Fact]
    public async Task Put_SendingNoPrinter_UnbindsTheConnection()
    {
        var instanceId = NewInstanceId();
        await PutAsync(instanceId, Body(printerId: IntegrationTestSeeder.TestPrinterId));

        var connection = await ReadAsync(await PutAsync(instanceId, Body(printerId: null)));

        Assert.Null(connection.PrinterId);
    }

    [Fact]
    public async Task Put_AnotherUsersPrinter_IsBadRequestAndRegistersNothing()
    {
        var foreignPrinter = await CreatePrinterAsync(IntegrationTestSeeder.SecondaryUserOAuthId);
        var instanceId = NewInstanceId();

        var response = await PutAsync(instanceId, Body(printerId: foreignPrinter));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(await ListAsync(), c => c.InstanceId == instanceId);
    }

    [Theory]
    [InlineData(null, "Bridge")]
    [InlineData("   ", "Bridge")]
    [InlineData("moonraker", null)]
    [InlineData("moonraker", "   ")]
    public async Task Put_MissingKindOrDisplayName_IsBadRequest(string? kind, string? displayName)
    {
        var response = await PutAsync(NewInstanceId(), new PutConnectionDto { Kind = kind, DisplayName = displayName });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_InstanceIdLongerThan100_IsBadRequest()
    {
        var response = await PutAsync(new string('a', 101), Body());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Put_SameInstanceIdForTwoUsers_KeepsTwoSeparateConnections()
    {
        var instanceId = NewInstanceId();
        var mine = await ReadAsync(await PutAsync(instanceId, Body(displayName: "Mine")));

        var secondaryPrinter = await CreatePrinterAsync(IntegrationTestSeeder.SecondaryUserOAuthId);
        var theirs = await ReadAsync(await PutAsync(
            instanceId, Body(displayName: "Theirs", printerId: secondaryPrinter), IntegrationTestSeeder.SecondaryUserOAuthId));

        Assert.NotEqual(mine.Id, theirs.Id);
        Assert.Equal("Mine", Assert.Single(await ListAsync(), c => c.InstanceId == instanceId).DisplayName);
    }

    [Fact]
    public async Task Put_Unauthenticated_IsUnauthorized()
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Connections/{NewInstanceId()}")
        {
            Content = JsonContent.Create(Body()),
        };

        var response = await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region GET

    [Fact]
    public async Task Get_ListsOnlyTheCallersConnections()
    {
        var mine = NewInstanceId();
        await PutAsync(mine, Body());
        var secondaryPrinter = await CreatePrinterAsync(IntegrationTestSeeder.SecondaryUserOAuthId);
        var theirs = NewInstanceId();
        await PutAsync(theirs, Body(printerId: secondaryPrinter), IntegrationTestSeeder.SecondaryUserOAuthId);

        var list = await ListAsync();

        Assert.Contains(list, c => c.InstanceId == mine);
        Assert.DoesNotContain(list, c => c.InstanceId == theirs);
    }

    [Fact]
    public async Task Get_HeartbeatOlderThan15Minutes_IsStale()
    {
        var fresh = NewInstanceId();
        var stale = NewInstanceId();
        await PutAsync(fresh, Body());
        await PutAsync(stale, Body());
        await BackdateLastSeenAsync(fresh, TimeSpan.FromMinutes(14));
        await BackdateLastSeenAsync(stale, TimeSpan.FromMinutes(16));

        var list = await ListAsync();

        Assert.Equal(ConnectionStatus.Online, list.Single(c => c.InstanceId == fresh).Status);
        Assert.Equal(ConnectionStatus.Stale, list.Single(c => c.InstanceId == stale).Status);
    }

    [Fact]
    public async Task Get_ByInstanceId_ReturnsTheConnection()
    {
        var instanceId = NewInstanceId();
        var created = await ReadAsync(await PutAsync(instanceId, Body()));

        var response = await SendAsync(HttpMethod.Get, $"/api/Connections/{instanceId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(created.Id, (await ReadAsync(response)).Id);
    }

    [Fact]
    public async Task Get_AnotherUsersInstanceId_IsNotFound()
    {
        var secondaryPrinter = await CreatePrinterAsync(IntegrationTestSeeder.SecondaryUserOAuthId);
        var theirs = NewInstanceId();
        await PutAsync(theirs, Body(printerId: secondaryPrinter), IntegrationTestSeeder.SecondaryUserOAuthId);

        var response = await SendAsync(HttpMethod.Get, $"/api/Connections/{theirs}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region DELETE

    [Fact]
    public async Task Delete_RemovesTheConnection()
    {
        var instanceId = NewInstanceId();
        await PutAsync(instanceId, Body());

        var response = await SendAsync(HttpMethod.Delete, $"/api/Connections/{instanceId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(await ListAsync(), c => c.InstanceId == instanceId);
    }

    [Fact]
    public async Task Delete_KeepsThePrintsLoggedThroughIt()
    {
        var instanceId = NewInstanceId();
        await PutAsync(instanceId, Body(displayName: "Doomed bridge"));
        var print = await CreatePrintAsync(connectionInstanceId: instanceId);
        Assert.Equal("Doomed bridge", print.ConnectionDisplayName);

        var response = await SendAsync(HttpMethod.Delete, $"/api/Connections/{instanceId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var detail = await GetPrintAsync(print.Id);
        Assert.Equal(print.Title, detail.Title);
        Assert.Null(detail.ConnectionDisplayName);
    }

    [Fact]
    public async Task Delete_UnknownOrAnotherUsersConnection_IsNotFound()
    {
        var secondaryPrinter = await CreatePrinterAsync(IntegrationTestSeeder.SecondaryUserOAuthId);
        var theirs = NewInstanceId();
        await PutAsync(theirs, Body(printerId: secondaryPrinter), IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/api/Connections/{NewInstanceId()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"/api/Connections/{theirs}")).StatusCode);
        Assert.Single(await ListAsync(printerId: null, IntegrationTestSeeder.SecondaryUserOAuthId), c => c.InstanceId == theirs);
    }

    [Fact]
    public async Task DeletingThePrinter_UnbindsItsConnection()
    {
        var printerId = await CreatePrinterAsync(IntegrationTestSeeder.TestUserOAuthId);
        var instanceId = NewInstanceId();
        await PutAsync(instanceId, Body(printerId: printerId));

        var response = await SendAsync(HttpMethod.Delete, $"/api/Printers/{printerId}");

        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}");
        var connection = Assert.Single(await ListAsync(), c => c.InstanceId == instanceId);
        Assert.Null(connection.PrinterId);
    }

    #endregion

    #region Prints logged through a connection

    [Fact]
    public async Task CreatePrint_WithConnectionInstanceId_ShowsTheConnectionsDisplayName()
    {
        var instanceId = NewInstanceId();
        await PutAsync(instanceId, Body(displayName: "Voron bridge"));

        var print = await CreatePrintAsync(connectionInstanceId: instanceId);

        Assert.Equal("Voron bridge", print.ConnectionDisplayName);
        Assert.Equal("Voron bridge", (await GetPrintAsync(print.Id)).ConnectionDisplayName);
    }

    [Fact]
    public async Task CreatePrint_RenamingTheConnection_RenamesWhatThePrintShows()
    {
        var instanceId = NewInstanceId();
        await PutAsync(instanceId, Body(displayName: "Before"));
        var print = await CreatePrintAsync(connectionInstanceId: instanceId);

        await PutAsync(instanceId, Body(displayName: "After"));

        Assert.Equal("After", (await GetPrintAsync(print.Id)).ConnectionDisplayName);
    }

    [Fact]
    public async Task CreatePrint_WithAnUnknownConnectionInstanceId_StillCreatesThePrint()
    {
        var response = await SendAsync(HttpMethod.Post, "/api/Prints", PrintBody(NewInstanceId()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var print = (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
        Assert.Null(print.ConnectionDisplayName);
    }

    [Fact]
    public async Task CreatePrint_WithAnotherUsersConnectionInstanceId_DoesNotLinkIt()
    {
        var secondaryPrinter = await CreatePrinterAsync(IntegrationTestSeeder.SecondaryUserOAuthId);
        var theirs = NewInstanceId();
        await PutAsync(theirs, Body(displayName: "Not yours", printerId: secondaryPrinter), IntegrationTestSeeder.SecondaryUserOAuthId);

        var print = await CreatePrintAsync(connectionInstanceId: theirs);

        Assert.Null(print.ConnectionDisplayName);
    }

    [Fact]
    public async Task GetPrint_ConnectionDisplayName_IsHiddenFromEveryoneButTheCreator()
    {
        var instanceId = NewInstanceId();
        await PutAsync(instanceId, Body(displayName: "Private bridge name"));
        var print = await CreatePrintAsync(connectionInstanceId: instanceId, viewStatus: PrintViewStatus.Public);
        Assert.Equal("Private bridge name", (await GetPrintAsync(print.Id)).ConnectionDisplayName);

        var anonymous = await _httpClient.GetAsync($"/api/Prints/{print.Id}", TestContext.Current.CancellationToken);
        var otherUser = await SendAsync(HttpMethod.Get, $"/api/Prints/{print.Id}", userOAuthId: IntegrationTestSeeder.SecondaryUserOAuthId);

        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.Null((await anonymous.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!.ConnectionDisplayName);
        Assert.Equal(HttpStatusCode.OK, otherUser.StatusCode);
        Assert.Null((await otherUser.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!.ConnectionDisplayName);
    }

    #endregion

    #region Helpers

    private static string NewInstanceId() => Guid.NewGuid().ToString();

    /// <summary>Stands in for the seeded printer, which is not a constant and so cannot be a default.</summary>
    private const long SeededPrinter = long.MinValue;

    private static PutConnectionDto Body(
        string? kind = "moonraker",
        string? displayName = "Test bridge",
        string? agentVersion = "0.1.0",
        long? printerId = SeededPrinter) => new()
        {
            Kind = kind,
            DisplayName = displayName,
            AgentVersion = agentVersion,
            PrinterId = printerId == SeededPrinter ? IntegrationTestSeeder.TestPrinterId : printerId,
        };

    private Task<HttpResponseMessage> PutAsync(string instanceId, PutConnectionDto body, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
        => SendAsync(HttpMethod.Put, $"/api/Connections/{Uri.EscapeDataString(instanceId)}", body, userOAuthId);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, userOAuthId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }
        return await _httpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<ConnectionDto> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<ConnectionDto>(TestContext.Current.CancellationToken))!;
    }

    private async Task<List<ConnectionDto>> ListAsync(long? printerId = null, string userOAuthId = IntegrationTestSeeder.TestUserOAuthId)
    {
        var url = printerId is { } id ? $"/api/Connections?printerId={id}" : "/api/Connections";
        var response = await SendAsync(HttpMethod.Get, url, userOAuthId: userOAuthId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<ConnectionDto>>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Moves a connection's last heartbeat into the past, as if the agent had gone quiet.</summary>
    private async Task BackdateLastSeenAsync(string instanceId, TimeSpan by)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var connection = await context.Connections.SingleAsync(
            c => c.InstanceId == instanceId && c.User.OAuthUserId == IntegrationTestSeeder.TestUserOAuthId,
            TestContext.Current.CancellationToken);
        connection.LastSeenAt = DateTime.UtcNow - by;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static AddPrintDTO PrintBody(string? connectionInstanceId, PrintViewStatus viewStatus = PrintViewStatus.Private) => new()
    {
        Title = $"connection test {Guid.NewGuid():N}",
        PrinterId = IntegrationTestSeeder.TestPrinterId,
        Status = PrintStatus.Printing,
        ViewStatus = viewStatus,
        ExternalSource = "moonraker",
        ExternalId = Guid.NewGuid().ToString(),
        ConnectionInstanceId = connectionInstanceId,
        FilamentUsage = [],
    };

    private async Task<PrintDetailDTO> CreatePrintAsync(string? connectionInstanceId, PrintViewStatus viewStatus = PrintViewStatus.Private)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/Prints", PrintBody(connectionInstanceId, viewStatus));
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
    }

    private async Task<PrintDetailDTO> GetPrintAsync(long id)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/Prints/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(TestContext.Current.CancellationToken))!;
    }

    private async Task<long> CreatePrinterAsync(string userOAuthId)
    {
        var response = await SendAsync(
            HttpMethod.Post,
            "/api/Printers",
            new AddPrinterDTO { Name = $"Connection test printer {Guid.NewGuid():N}", Make = "Make", Model = "Model", IsActive = true },
            userOAuthId);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<PrinterDetailDto>(TestContext.Current.CancellationToken))!.Id!.Value;
    }

    #endregion
}
