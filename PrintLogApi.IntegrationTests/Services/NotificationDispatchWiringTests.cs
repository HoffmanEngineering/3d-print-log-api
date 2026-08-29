using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Services;
using PrintLogApi.Services.Push;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

public class RecordingPushDispatchService : IPushDispatchService
{
    public List<Notification> Dispatched { get; } = [];
    public Exception? ThrowOnDispatch { get; set; }

    public Task DispatchForNotification(Notification notification, CancellationToken ct = default)
    {
        if (ThrowOnDispatch is not null)
        {
            throw ThrowOnDispatch;
        }

        Dispatched.Add(notification);
        return Task.CompletedTask;
    }
}

public class NotificationDispatchWiringTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public NotificationDispatchWiringTests(CustomWebApplicationFactory factory) => _factory = factory;

    private (NotificationService Service, RecordingPushDispatchService Dispatch, PrintLogContext Db, IServiceScope Scope) Build()
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var mapper = scope.ServiceProvider.GetRequiredService<IMapper>();
        var dispatch = new RecordingPushDispatchService();
        return (new NotificationService(db, mapper, dispatch), dispatch, db, scope);
    }

    /// <summary>
    /// Notification.PrintId and .CommentId are real foreign keys, so these tests have to hang
    /// their notifications off seeded rows; an arbitrary id trips the constraint before the
    /// dispatch wiring is ever exercised.
    /// </summary>
    private static long CreateComment(PrintLogContext db)
    {
        var comment = new Comment
        {
            Body = "dispatch wiring test",
            CreatedById = IntegrationTestSeeder.TestUserId,
            UpdatedById = IntegrationTestSeeder.TestUserId,
            CreatedDate = DateTime.UtcNow,
            UpdatedDate = DateTime.UtcNow
        };
        db.Comments.Add(comment);
        db.SaveChanges();
        return comment.Id;
    }

    [Fact]
    public async Task PrintFailedNotification_ReachesTheDispatcher()
    {
        var (service, dispatch, _, scope) = Build();
        using var _s = scope;

        await service.CreatePrintFailedNotification(
            IntegrationTestSeeder.TestUserId, IntegrationTestSeeder.TestPrintId, "Benchy");

        Assert.Single(dispatch.Dispatched);
        Assert.Equal(NotificationType.PrintFailed, dispatch.Dispatched[0].Type);
    }

    [Fact]
    public async Task CommentNotifications_ReachTheSameDispatcher()
    {
        var (service, dispatch, _db, scope) = Build();
        using var _s = scope;

        // Guards the refactor: this path used to save directly, so a dispatcher wired only
        // into CreateNotification would silently skip every comment notification.
        var commentId = CreateComment(_db!);

        await service.CreateCommentNotifications(
            [(IntegrationTestSeeder.TestUserId, true)],
            printId: IntegrationTestSeeder.TestPrintId,
            printTitle: "Benchy",
            commentId: commentId,
            commenterUserId: IntegrationTestSeeder.SecondaryUserId,
            commenterDisplayName: "Someone");

        Assert.Single(dispatch.Dispatched);
        Assert.Equal(NotificationType.Comment, dispatch.Dispatched[0].Type);
    }

    [Fact]
    public async Task DispatchFailure_DoesNotPreventThePersistedNotification()
    {
        var (service, dispatch, db, scope) = Build();
        using var _s = scope;
        dispatch.ThrowOnDispatch = new InvalidOperationException("FCM down");

        // The real dispatcher swallows its own errors; this asserts the chokepoint does not
        // let a dispatcher failure roll back or hide the notification row.
        var created = await service.CreatePrintFailedNotification(
            IntegrationTestSeeder.TestUserId, IntegrationTestSeeder.TestPrintId, "Benchy");

        Assert.NotEmpty(db.Notifications.Where(n => n.Id == created.Id));
    }
}
