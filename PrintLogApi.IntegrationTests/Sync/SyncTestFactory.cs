using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PrintLogApi.IntegrationTests.Analytics;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Sync;

/// <summary>
/// The standard host with a <see cref="SettableTimeProvider"/> in place of the system clock, so a
/// connection can be made stale by moving the clock rather than by waiting 15 minutes. Tests
/// arrange their own users, so nothing they assert on is shared with a sibling.
/// </summary>
public sealed class SyncTestFactory : CustomWebApplicationFactory
{
    public static readonly DateTimeOffset Start = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    public SettableTimeProvider Clock { get; } = new(Start);

    /// <summary>Every statement the app's own context runs, for asserting what a request did not query.</summary>
    public CommandRecorder Commands { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.ConfigureDbContext<PrintLogContext>(options => options.AddInterceptors(Commands));
        });
    }

    /// <summary>A fresh user, so a test's printers and spools are the only ones it sees.</summary>
    public async Task<User> CreateUserAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var user = new User
        {
            OAuthUserId = $"auth0|sync-{Guid.NewGuid():N}",
            DisplayName = "Sync",
            ViewStatus = User.ProfileViewStatus.Private,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user;
    }

    public HttpRequestMessage Get(User user, string url, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return request;
    }

    /// <summary>Bumps the user's cache version, as every write does.</summary>
    public void Invalidate(User user) =>
        Services.GetRequiredService<PrintLogApi.Services.ICacheVersionService>().InvalidateUserCache(user.Id);
}

/// <summary>Records each statement's text. Start a window with <see cref="Clear"/>.</summary>
public sealed class CommandRecorder : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();

    public IReadOnlyList<string> Recorded => _commands.ToArray();

    public void Clear() => _commands.Clear();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        _commands.Enqueue(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);
        return ValueTask.FromResult(result);
    }
}
