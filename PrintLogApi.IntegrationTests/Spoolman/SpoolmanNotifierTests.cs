using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Controllers;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Moonraker;
using PrintLogApi.Models.DTOs.UserApiKeys;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Spoolman;

/// <summary>
/// <c>POST /spoolman/notifier</c>: Moonraker's notifier events over the printer's Spoolman
/// credential, so one pasted block both powers the Spoolman integration and logs prints.
/// </summary>
public class SpoolmanNotifierTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private const string Route = "/spoolman/notifier";

    private readonly HttpClient _client = factory.CreateClient();
    private readonly SpoolmanTestSupport _support = new(factory.Services);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Started_CreatesAPrintingPrintOnTheConnectionsPrinter()
    {
        var user = await _support.CreateUserAsync();
        var printer = await _support.CreatePrinterAsync(user);
        var other = await _support.CreatePrinterAsync(user);
        var connection = await _support.ConnectAsync(user, printer);
        var file = UniqueFile();

        // The body names another printer of the same user; the credential's printer wins.
        var response = await PostAsync(connection.Secret!, Event("started", other, file));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var print = Assert.Single(await PrintsAsync(user, file));
        Assert.Equal(PrintStatus.Printing, print.Status);
        Assert.Equal(printer, print.PrinterId);
        Assert.Equal(MoonrakerController.NotifierSource, print.ExternalSource);
    }

    [Fact]
    public async Task Complete_MarksThePrintSuccess()
    {
        var (user, secret, printer) = await ConnectedAsync();
        var file = UniqueFile();

        await PostAsync(secret, Event("started", printer, file));
        var response = await PostAsync(secret, Event("complete", printer, file, totalDuration: 120));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PrintStatus.Success, Assert.Single(await PrintsAsync(user, file)).Status);
    }

    [Fact]
    public async Task Cancelled_MarksThePrintFailed()
    {
        var (user, secret, printer) = await ConnectedAsync();
        var file = UniqueFile();

        await PostAsync(secret, Event("started", printer, file));
        var response = await PostAsync(secret, Event("cancelled", printer, file, printDuration: 30));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PrintStatus.Failed, Assert.Single(await PrintsAsync(user, file)).Status);
    }

    [Fact]
    public async Task NoCredential_Is401()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = Event("started", 1, UniqueFile()) };

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task ApiKey_Is401()
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys");
        create.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        create.Content = JsonContent.Create(new AddNewApiKeyDto { Description = "Notifier test" });
        var created = await _client.SendAsync(create, Ct);
        var key = (await created.Content.ReadFromJsonAsync<NewUserApiKeyDto>(Ct))!;

        var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = Event("started", 1, UniqueFile()) };
        request.Headers.Add("X-Api-Key", key.PublicKey);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task ConnectionWithoutPrinter_Is400()
    {
        var (_, secret, printer) = await ConnectedAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await db.SpoolmanConnections.ExecuteUpdateAsync(s => s.SetProperty(c => c.PrinterId, (long?)null), Ct);
        }

        var response = await PostAsync(secret, Event("started", printer, UniqueFile()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("This connection isn't linked to a printer.", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task BadJson_Is400WithAMessage()
    {
        var (_, secret, _) = await ConnectedAsync();

        var response = await PostAsync(secret, new StringContent("not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await SpoolmanTestSupport.AssertSpoolmanErrorAsync(response);
    }

    [Fact]
    public Task LiveBridge_DropsTheEvent() => AssertNotifierEventWithLiveConnection(Connection.MoonrakerKind, logged: false);

    [Theory]
    [InlineData("octoprint")]
    [InlineData("bambu")]
    public Task LiveNonMoonrakerBridge_KeepsTheEvent(string kind)
    {
        // The dedupe stays Moonraker's (#150, printlog-bridge#82): the drop count drives the
        // "remove [notifier] from moonraker.conf" notice on that connection. The config block
        // leaves the notifier out for any bridge, so this only reaches a hand-added one.
        return AssertNotifierEventWithLiveConnection(kind, logged: true);
    }

    private async Task AssertNotifierEventWithLiveConnection(string kind, bool logged)
    {
        var (user, secret, printer) = await ConnectedAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            db.Connections.Add(new Connection
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Kind = kind,
                InstanceId = Guid.NewGuid().ToString("N"),
                DisplayName = "Bridge",
                PrinterId = printer,
                CreatedDate = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }
        var file = UniqueFile();

        var response = await PostAsync(secret, Event("started", printer, file));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(logged ? 1 : 0, (await PrintsAsync(user, file)).Count);
    }

    private async Task<(User User, string Secret, long Printer)> ConnectedAsync()
    {
        var user = await _support.CreateUserAsync();
        var printer = await _support.CreatePrinterAsync(user);
        var connection = await _support.ConnectAsync(user, printer);
        return (user, connection.Secret!, printer);
    }

    private static string UniqueFile() => $"notifier-{Guid.NewGuid():N}.gcode";

    private Task<HttpResponseMessage> PostAsync(string secret, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = content };
        request.Headers.Authorization = SpoolmanTestSupport.Basic(secret);
        return _client.SendAsync(request, Ct);
    }

    /// <summary>The Apprise envelope Moonraker posts: a JSON object whose <c>message</c> is the event as a JSON string.</summary>
    private static StringContent Event(string name, long printerId, string file, double totalDuration = 0, double printDuration = 0)
    {
        var inner = JsonSerializer.Serialize(new PrintEventMessageDto
        {
            EventName = name,
            Filename = file,
            PrinterId = printerId,
            TotalDuration = totalDuration,
            PrintDuration = printDuration,
        });
        var outer = JsonSerializer.Serialize(new PrintEventDto { Message = inner }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new StringContent(outer, Encoding.UTF8, "application/json");
    }

    private async Task<List<Print>> PrintsAsync(User user, string file)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return await db.Prints.Where(p => p.CreatedById == user.Id && p.FileName == file).ToListAsync(Ct);
    }
}
