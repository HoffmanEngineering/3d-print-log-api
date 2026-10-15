using System.Net.Http.Headers;
using System.Text;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Spoolman;
using PrintLogApi.Services.Spoolman;
using Xunit;

namespace PrintLogApi.IntegrationTests.Spoolman;

/// <summary>
/// Arranges what every Spoolman test needs: a fresh user (Free or Pro), a printer, and a real
/// connection minted by the production service, so the secret a test presents is hashed and
/// looked up exactly as Moonraker's would be.
/// </summary>
public sealed class SpoolmanTestSupport(IServiceProvider services)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<User> CreateUserAsync(bool pro = false)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|spoolman-{Guid.NewGuid():N}",
            DisplayName = "Spoolman",
            ViewStatus = User.ProfileViewStatus.Private,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(Ct);

        if (pro)
        {
            await SetProAsync(user, true);
        }

        return user;
    }

    /// <summary>Makes the user Pro or not by writing the subscription row directly, without applying the tier.</summary>
    public async Task SetProAsync(User user, bool pro)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var subscription = db.Subscriptions.SingleOrDefault(s => s.UserId == user.Id);
        if (subscription is null)
        {
            subscription = new Subscription
            {
                UserId = user.Id,
                Plan = SubscriptionPlan.ProMonthly,
                StripeCustomerId = $"cus_{user.Id}",
                StripeSubscriptionId = $"sub_{user.Id}",
                CreatedById = user.Id,
                UpdatedById = user.Id,
            };
            db.Subscriptions.Add(subscription);
        }

        subscription.Status = pro ? SubscriptionStatus.Active : SubscriptionStatus.Canceled;
        await db.SaveChangesAsync(Ct);
    }

    public async Task<long> CreatePrinterAsync(User user, string? name = null)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var printer = new Printer
        {
            Name = name ?? $"Printer {Guid.NewGuid():N}"[..20],
            UserId = user.Id,
            IsActive = true,
            CategoryNickname = "FDM",
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync(Ct);
        return printer.Id;
    }

    /// <summary>A connection made through the production service, secret included.</summary>
    public async Task<SpoolmanConnectionCreatedDto> ConnectAsync(User user, long? printerId = null)
    {
        printerId ??= await CreatePrinterAsync(user);
        using var scope = services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<ISpoolmanConnectionService>()
            .CreateAsync(user.Id, printerId.Value, "https://api.3dprintlog.test", Ct);
        return created ?? throw new InvalidOperationException("The printer is not the user's.");
    }

    /// <summary>Spoolman's error shape: <c>{"message": "..."}</c> and nothing else, as JSON.</summary>
    public static async Task AssertSpoolmanErrorAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var property = Assert.Single(body.RootElement.EnumerateObject());
        Assert.Equal("message", property.Name);
        Assert.False(string.IsNullOrWhiteSpace(property.Value.GetString()));
    }

    /// <summary>What Moonraker sends for <c>https://printer-&lt;id&gt;:&lt;secret&gt;@host/spoolman</c>.</summary>
    public static AuthenticationHeaderValue Basic(string secret, string user = "printer-1") =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));
}
