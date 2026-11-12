using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Comments;
using Xunit;
using static PrintLogApi.IntegrationTests.Controllers.PublicReadSeed;

namespace PrintLogApi.IntegrationTests.Controllers;

/// <summary>
/// <c>POST api/Prints/{id}/comment</c> limits: per commenter per print, at most
/// <see cref="CommentLimits.PerCommenterShortWindowMax"/> in <see cref="CommentLimits.ShortWindow"/>
/// and <see cref="CommentLimits.PerCommenterDayMax"/> in <see cref="CommentLimits.DayWindow"/>
/// (the print's owner is exempt on their own print), and at most
/// <see cref="CommentLimits.MaxCommentsPerPrint"/> on a print in total (owner included). A refused
/// comment writes no row and sends no notification.
/// </summary>
public class PrintCommentLimitTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Owner Owner, Print Print)> CommentablePrintAsync()
    {
        var owner = await OwnerAsync(factory);
        var print = await PrintAsync(factory, owner);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        await db.Prints.Where(p => p.Id == print.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.AllowComments, true), Ct);
        return (owner, print);
    }

    private async Task<HttpResponseMessage> PostAsync(long printId, User user, string body = "Nice print")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{printId}/comment")
        {
            Content = JsonContent.Create(new AddCommentDto { Body = body }),
        };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        return await _client.SendAsync(request, Ct);
    }

    /// <summary>Writes comments straight to the database, created <paramref name="age"/> ago, with no notifications.</summary>
    private async Task SeedCommentsAsync(long printId, long userId, int count, TimeSpan age)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var comments = Enumerable.Range(0, count)
            .Select(i => new Comment { Body = $"seeded {i}", CreatedById = userId, UpdatedById = userId })
            .ToList();
        db.Comments.AddRange(comments);
        db.PrintComments.AddRange(comments.Select(c => new PrintComment
        {
            PrintId = printId,
            Comment = c,
            CreatedById = userId,
            UpdatedById = userId,
        }));
        await db.SaveChangesAsync(Ct);

        // SaveChanges stamps CreatedDate with now; backdate the rows afterwards.
        var ids = comments.Select(c => c.Id).ToList();
        var created = DateTime.UtcNow - age;
        await db.Comments.Where(c => ids.Contains(c.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedDate, created), Ct);
    }

    private async Task<(int Comments, int Notifications)> CountsAsync(long printId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        return (await db.PrintComments.CountAsync(pc => pc.PrintId == printId, Ct),
                await db.Notifications.CountAsync(n => n.PrintId == printId, Ct));
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task ACommenter_IsRefusedPastTheShortWindowLimit_WithNoRowAndNoNotification()
    {
        var (_, print) = await CommentablePrintAsync();
        var earlier = await OtherUserAsync(factory);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(print.Id, earlier)).StatusCode);
        var commenter = await OtherUserAsync(factory);

        for (var i = 0; i < CommentLimits.PerCommenterShortWindowMax; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(print.Id, commenter, $"comment {i}")).StatusCode);
        }

        var before = await CountsAsync(print.Id);
        var refused = await PostAsync(print.Id, commenter, "one too many");

        await AssertProblemAsync(refused, HttpStatusCode.TooManyRequests, CommentLimits.RateLimitedCode);
        var retryAfter = refused.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter.Value, TimeSpan.FromSeconds(1), CommentLimits.ShortWindow);
        Assert.Equal(before, await CountsAsync(print.Id));
    }

    [Fact]
    public async Task CommentsOlderThanTheShortWindow_DoNotCountTowardIt()
    {
        var (_, print) = await CommentablePrintAsync();
        var commenter = await OtherUserAsync(factory);
        await SeedCommentsAsync(print.Id, commenter.Id, CommentLimits.PerCommenterShortWindowMax,
            CommentLimits.ShortWindow + TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(print.Id, commenter)).StatusCode);
    }

    [Fact]
    public async Task ACommenter_IsRefusedPastTheDayLimit()
    {
        var (_, print) = await CommentablePrintAsync();
        var commenter = await OtherUserAsync(factory);
        await SeedCommentsAsync(print.Id, commenter.Id, CommentLimits.PerCommenterDayMax - 1, TimeSpan.FromHours(1));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(print.Id, commenter)).StatusCode);

        var before = await CountsAsync(print.Id);
        var refused = await PostAsync(print.Id, commenter);

        await AssertProblemAsync(refused, HttpStatusCode.TooManyRequests, CommentLimits.RateLimitedCode);
        var retryAfter = refused.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        // The seeded comments age out of the day window in about 23 hours.
        Assert.InRange(retryAfter.Value, TimeSpan.FromHours(22), CommentLimits.DayWindow);
        Assert.Equal(before, await CountsAsync(print.Id));
    }

    [Fact]
    public async Task CommentsOlderThanADay_DoNotCountTowardTheDayLimit()
    {
        var (_, print) = await CommentablePrintAsync();
        var commenter = await OtherUserAsync(factory);
        await SeedCommentsAsync(print.Id, commenter.Id, CommentLimits.PerCommenterDayMax,
            CommentLimits.DayWindow + TimeSpan.FromHours(1));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(print.Id, commenter)).StatusCode);
    }

    [Fact]
    public async Task TheLimitsArePerPrint()
    {
        var (owner, print) = await CommentablePrintAsync();
        var commenter = await OtherUserAsync(factory);
        await SeedCommentsAsync(print.Id, commenter.Id, CommentLimits.PerCommenterShortWindowMax, TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(print.Id, commenter)).StatusCode);

        var otherPrint = await PrintAsync(factory, owner, title: "Another benchy");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            await db.Prints.Where(p => p.Id == otherPrint.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.AllowComments, true), Ct);
        }

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(otherPrint.Id, commenter)).StatusCode);
    }

    [Fact]
    public async Task TheOwner_IsExemptFromThePerCommenterLimitsOnTheirOwnPrint()
    {
        var (owner, print) = await CommentablePrintAsync();
        await SeedCommentsAsync(print.Id, owner.User.Id, CommentLimits.PerCommenterDayMax, TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(print.Id, owner.User, "Thanks, everyone")).StatusCode);
    }

    [Fact]
    public async Task APrintAtTheTotal_RefusesEveryone_IncludingTheOwner()
    {
        var (owner, print) = await CommentablePrintAsync();
        var seeder = await OtherUserAsync(factory);
        await SeedCommentsAsync(print.Id, seeder.Id, CommentLimits.MaxCommentsPerPrint - 1, TimeSpan.FromDays(3));
        var commenter = await OtherUserAsync(factory);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(print.Id, commenter)).StatusCode);

        var before = await CountsAsync(print.Id);
        Assert.Equal(CommentLimits.MaxCommentsPerPrint, before.Comments);

        await AssertProblemAsync(await PostAsync(print.Id, commenter), HttpStatusCode.BadRequest, CommentLimits.TotalReachedCode);
        await AssertProblemAsync(await PostAsync(print.Id, owner.User), HttpStatusCode.BadRequest, CommentLimits.TotalReachedCode);
        Assert.Equal(before, await CountsAsync(print.Id));
    }
}
