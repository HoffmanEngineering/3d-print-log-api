using System.Net;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.UserApiKeys;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Achievements;

public class PrintProvenanceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PrintProvenanceTests(CustomWebApplicationFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AddPrintDTO NewPrint(long printerId, Guid? curaSettingId = null) => new()
    {
        Title = "Provenance",
        PrinterId = printerId,
        Status = Print.PrintStatus.Success,
        ViewStatus = Print.PrintViewStatus.Private,
        FilamentUsage = [],
        CuraSettingId = curaSettingId,
    };

    private static async Task<CuraSetting> AddSettingAsync(PrintLogContext db, long? ownerId, string? slicer, string? version = "5.4.0")
    {
        var setting = new CuraSetting
        {
            CuraVersion = version,
            PluginVersion = "1.2.0",
            Slicer = slicer,
            CreatedDate = DateTimeOffset.UtcNow,
            UserId = ownerId,
        };
        db.CuraSettings.Add(setting);
        await db.SaveChangesAsync(Ct);
        return setting;
    }

    private async Task<(IServiceScope Scope, PrintLogContext Db, IPrintService Prints, User User, Printer Printer)> ArrangeAsync()
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = await AchievementTestData.CreateUserAsync(db);
        var printer = await AchievementTestData.AddPrinterAsync(db, user);
        return (scope, db, scope.ServiceProvider.GetRequiredService<IPrintService>(), user, printer);
    }

    [Fact]
    public async Task AddPrint_WithOwnCuraSetting_RecordsSlicerPlugin()
    {
        var (scope, db, prints, user, printer) = await ArrangeAsync();
        using var _ = scope;
        var setting = await AddSettingAsync(db, user.Id, "BambuStudioSlicer", "02.02.00");

        var print = await prints.AddPrint(NewPrint(printer.Id, setting.Id), user.Id);

        Assert.Equal(PrintSource.SlicerPlugin, print.Source);
        Assert.Equal("bambustudio", print.Slicer);
        Assert.Equal("02.02.00", print.SlicerVersion);
    }

    [Fact]
    public async Task AddPrint_WithNullSlicerSetting_RecordsCura()
    {
        var (scope, db, prints, user, printer) = await ArrangeAsync();
        using var _ = scope;
        var setting = await AddSettingAsync(db, user.Id, slicer: null);

        var print = await prints.AddPrint(NewPrint(printer.Id, setting.Id), user.Id);

        Assert.Equal(PrintSource.SlicerPlugin, print.Source);
        Assert.Equal("cura", print.Slicer);
    }

    [Fact]
    public async Task AddPrint_ForeignOrMissingCuraSetting_RecordsWebAndSucceeds()
    {
        var (scope, db, prints, user, printer) = await ArrangeAsync();
        using var _ = scope;
        var other = await AchievementTestData.CreateUserAsync(db);
        var foreign = await AddSettingAsync(db, other.Id, "PrusaSlicer");

        var fromForeign = await prints.AddPrint(NewPrint(printer.Id, foreign.Id), user.Id);
        var fromMissing = await prints.AddPrint(NewPrint(printer.Id, Guid.NewGuid()), user.Id);

        Assert.Equal(PrintSource.Web, fromForeign.Source);
        Assert.Null(fromForeign.Slicer);
        Assert.Equal(PrintSource.Web, fromMissing.Source);
        Assert.Null(fromMissing.Slicer);
    }

    [Fact]
    public async Task AddPrint_ApiKeyCandidate_WithValidSetting_PrefersSlicerPlugin()
    {
        var (scope, db, prints, user, printer) = await ArrangeAsync();
        using var _ = scope;
        var setting = await AddSettingAsync(db, user.Id, "OrcaSlicer");

        var print = await prints.AddPrint(NewPrint(printer.Id, setting.Id), user.Id, PrintSource.ApiKey);

        Assert.Equal(PrintSource.SlicerPlugin, print.Source);
        Assert.Equal("orcaslicer", print.Slicer);
    }

    [Fact]
    public async Task AddPrint_ApiKeyCandidate_NoSetting_RecordsApiKey()
    {
        var (scope, _, prints, user, printer) = await ArrangeAsync();
        using var _s = scope;

        var print = await prints.AddPrint(NewPrint(printer.Id), user.Id, PrintSource.ApiKey);

        Assert.Equal(PrintSource.ApiKey, print.Source);
        Assert.Null(print.Slicer);
    }

    [Fact]
    public async Task PostPrint_ViaApiKey_RecordsApiKey()
    {
        var client = _factory.CreateClient();
        var keyRequest = new HttpRequestMessage(HttpMethod.Post, "/api/UserApiKeys");
        keyRequest.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        keyRequest.Content = JsonContent.Create(new AddNewApiKeyDto { Description = "Provenance" });
        var keyResponse = await client.SendAsync(keyRequest, Ct);
        Assert.Equal(HttpStatusCode.OK, keyResponse.StatusCode);
        var key = (await keyResponse.Content.ReadFromJsonAsync<NewUserApiKeyDto>(Ct))!;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add("X-Api-Key", key.PublicKey);
        request.Content = JsonContent.Create(NewPrint(IntegrationTestSeeder.TestPrinterId));
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(Ct))!;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var source = await db.Prints.Where(p => p.Id == created.Id).Select(p => p.Source).SingleAsync(Ct);
        Assert.Equal(PrintSource.ApiKey, source);
    }

    [Fact]
    public async Task PostPrint_Interactive_RecordsWeb()
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints");
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
        request.Content = JsonContent.Create(NewPrint(IntegrationTestSeeder.TestPrinterId));
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(Ct))!;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var source = await db.Prints.Where(p => p.Id == created.Id).Select(p => p.Source).SingleAsync(Ct);
        Assert.Equal(PrintSource.Web, source);
    }

    [Fact]
    public async Task CreatePrintForMcp_RecordsMcp()
    {
        var (scope, db, prints, user, printer) = await ArrangeAsync();
        using var _s = scope;

        var result = await prints.CreatePrintForMcp(user.Id, "Robot print", printer.Id, Print.PrintStatus.Success,
            DateTimeOffset.UtcNow, 600, null, null, null, null, null, null, null, null, [], $"prov-{Guid.NewGuid():N}", Ct);

        var source = await db.Prints.Where(p => p.Id == result.Print.Id).Select(p => p.Source).SingleAsync(Ct);
        Assert.Equal(PrintSource.Mcp, source);
    }
}
