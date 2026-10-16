using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;
using PrintLogApi.Models.DTOs.Webhook;
using PrintLogApi.Services;
using PrintLogApi.Webhooks;
using Xunit;
using static PrintLogApi.Models.Print;

namespace PrintLogApi.IntegrationTests.Webhooks;

/// <summary>
/// Print events reaching webhooks (#235): from the finish choke point (#166) and the start choke
/// point, with the payload, signature, retries and auto-disable. Each test uses its own user and
/// receiving host, and delivers the queue itself, so nothing depends on timing.
/// </summary>
public class WebhookDispatchTests : IClassFixture<WebhookTestFactory>
{
    private readonly WebhookTestFactory _factory;
    private readonly HttpClient _client;

    public WebhookDispatchTests(WebhookTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        factory.DiscardPending();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Arranged(User User, long PrinterId, string Host, WebhookSubscriptionCreatedDto Subscription);

    private async Task<Arranged> ArrangeAsync(string[]? events = null, bool pro = false)
    {
        var user = await _factory.CreateUserAsync(pro);
        var printerId = await _factory.CreatePrinterAsync(user, "Voron 2.4");
        var host = $"ha-{Guid.NewGuid():N}.example.test";
        var subscription = await SubscribeAsync(user, host, events);
        return new Arranged(user, printerId, host, subscription);
    }

    private async Task<WebhookSubscriptionCreatedDto> SubscribeAsync(User user, string host, string[]? events = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Webhooks")
        {
            Content = JsonContent.Create(new { url = $"https://{host}/api/webhook/secret-hook-id", events }),
        };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, user.OAuthUserId);
        var response = await _client.SendAsync(request, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<WebhookSubscriptionCreatedDto>(Ct))!;
    }

    private async Task<PrintDetailDTO> CreatePrintAsync(Arranged a, PrintStatus status, string title = "Benchy")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints")
        {
            Content = JsonContent.Create(new AddPrintDTO
            {
                Title = title,
                PrinterId = a.PrinterId,
                Status = status,
                ViewStatus = PrintViewStatus.Private,
                StartDate = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero),
                EstimatedPrintTimeInSeconds = 3_500,
                FilamentUsage = [],
            }),
        };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, a.User.OAuthUserId);
        var response = await _client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PrintDetailDTO>(Ct))!;
    }

    private async Task CompleteAsync(Arranged a, long printId, PrintStatus status, int? amountMg = 12_500)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/Prints/{printId}/complete")
        {
            Content = JsonContent.Create(new CompletePrintDto
            {
                Status = status,
                PrintTimeInSeconds = 3_600,
                FilamentUsage = amountMg is { } mg ? [new CompletePrintFilamentUsageDto { Slot = 0, AmountMg = mg }] : [],
            }),
        };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, a.User.OAuthUserId);
        var response = await _client.SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<WebhookSubscription> StoredAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PrintLogContext>().WebhookSubscriptions.AsNoTracking().SingleAsync(s => s.Id == id, Ct);
    }

    private static JsonElement Body(ReceivedWebhook received) => JsonDocument.Parse(received.Body).RootElement;

    #region Finished and failed

    [Fact]
    public async Task Finishing_SendsPrintFinished_WithTheVersionedPayload()
    {
        var a = await ArrangeAsync();
        var print = await CreatePrintAsync(a, PrintStatus.Printing);
        _factory.DiscardPending();

        await CompleteAsync(a, print.Id, PrintStatus.Success);
        await _factory.DeliverPendingAsync(Ct);

        var received = Assert.Single(_factory.Receiver.Received(a.Host));
        var body = Body(received);
        Assert.Equal(1, body.GetProperty("version").GetInt32());
        Assert.Equal("print.finished", body.GetProperty("event").GetString());
        Assert.StartsWith("evt_", body.GetProperty("id").GetString());
        Assert.True(body.TryGetProperty("createdAt", out _));
        var data = body.GetProperty("data");
        Assert.Equal(print.Id, data.GetProperty("print").GetProperty("id").GetInt64());
        Assert.Equal("Benchy", data.GetProperty("print").GetProperty("title").GetString());
        Assert.Equal("Success", data.GetProperty("print").GetProperty("status").GetString());
        Assert.Equal(3_600, data.GetProperty("print").GetProperty("durationSeconds").GetInt32());
        Assert.Equal(3_500, data.GetProperty("print").GetProperty("estimatedDurationSeconds").GetInt32());
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T12:00:00Z"), data.GetProperty("print").GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T13:00:00Z"), data.GetProperty("print").GetProperty("finishedAt").GetDateTimeOffset());
        Assert.EndsWith($"/prints/{print.Id}", data.GetProperty("print").GetProperty("url").GetString());
        Assert.Equal(a.PrinterId, data.GetProperty("printer").GetProperty("id").GetInt64());
        Assert.Equal("Voron 2.4", data.GetProperty("printer").GetProperty("name").GetString());
        var spool = Assert.Single(data.GetProperty("filament").EnumerateArray().ToList());
        Assert.Equal(12.5, spool.GetProperty("grams").GetDouble());
        Assert.Equal(0, spool.GetProperty("slot").GetInt32());
    }

    [Fact]
    public async Task TheDelivery_IsSignedWithTheSubscriptionSecret_AndCarriesTheEventId()
    {
        var a = await ArrangeAsync();
        var print = await CreatePrintAsync(a, PrintStatus.Printing);
        _factory.DiscardPending();

        await CompleteAsync(a, print.Id, PrintStatus.Success);
        await _factory.DeliverPendingAsync(Ct);

        var received = Assert.Single(_factory.Receiver.Received(a.Host));
        Assert.Equal(HttpMethod.Post, received.Method);
        Assert.StartsWith("application/json", received.Headers["Content-Type"]);
        Assert.Equal("print.finished", received.Headers["X-PrintLog-Event"]);
        Assert.Equal(Body(received).GetProperty("id").GetString(), received.Headers["X-PrintLog-Event-Id"]);
        Assert.Equal("1", received.Headers["X-PrintLog-Delivery-Attempt"]);
        var signature = received.Headers["X-PrintLog-Signature"];
        var timestamp = long.Parse(signature.Split(',')[0][2..]);
        Assert.InRange(timestamp, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5);
        Assert.Equal(WebhookSignature.Create(a.Subscription.Secret!, timestamp, Encoding.UTF8.GetBytes(received.Body)), signature);
    }

    [Theory]
    [InlineData(PrintStatus.Failed)]
    [InlineData(PrintStatus.Cancelled)]
    [InlineData(PrintStatus.PartialSuccess)]
    public async Task FailingOrCanceling_SendsPrintFailed(PrintStatus status)
    {
        var a = await ArrangeAsync();
        var print = await CreatePrintAsync(a, PrintStatus.Printing);
        _factory.DiscardPending();

        await CompleteAsync(a, print.Id, status);
        await _factory.DeliverPendingAsync(Ct);

        var body = Body(Assert.Single(_factory.Receiver.Received(a.Host)));
        Assert.Equal("print.failed", body.GetProperty("event").GetString());
        Assert.Equal(status.ToString(), body.GetProperty("data").GetProperty("print").GetProperty("status").GetString());
    }

    [Fact]
    public async Task TheEventFilter_IsHonored()
    {
        var a = await ArrangeAsync(events: ["print.finished"]);
        var print = await CreatePrintAsync(a, PrintStatus.Printing);

        await CompleteAsync(a, print.Id, PrintStatus.Failed);
        await _factory.DeliverPendingAsync(Ct);

        Assert.Empty(_factory.Receiver.Received(a.Host));
    }

    [Fact]
    public async Task CompletingAnAlreadyFinishedPrint_SendsNothing()
    {
        var a = await ArrangeAsync(events: ["print.finished"]);
        var print = await CreatePrintAsync(a, PrintStatus.Printing);
        await CompleteAsync(a, print.Id, PrintStatus.Success);
        await _factory.DeliverPendingAsync(Ct);

        await CompleteAsync(a, print.Id, PrintStatus.Success);
        await _factory.DeliverPendingAsync(Ct);

        Assert.Single(_factory.Receiver.Received(a.Host));
    }

    [Fact]
    public async Task ThePrintEventPipeline_SendsFinished_ThroughTheSameChokePoint()
    {
        var a = await ArrangeAsync(events: ["print.finished"]);
        using (var scope = _factory.Services.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IPrintEventService>();
            await events.Started(StartedEvent(a, "pipeline-finish"));
            await events.Finished(new PrintFinishedEvent
            {
                UserId = a.User.Id,
                PrinterId = a.PrinterId,
                FileName = "pipeline-finish.gcode",
                Status = PrintStatus.Success,
                PrintTimeInSeconds = 600,
            });
        }

        await _factory.DeliverPendingAsync(Ct);

        var body = Body(Assert.Single(_factory.Receiver.Received(a.Host)));
        Assert.Equal("print.finished", body.GetProperty("event").GetString());
        Assert.Equal("pipeline-finish", body.GetProperty("data").GetProperty("print").GetProperty("title").GetString());
    }

    #endregion

    #region Started

    [Fact]
    public async Task CreatingAPrintingPrint_SendsPrintStarted()
    {
        var a = await ArrangeAsync();

        var print = await CreatePrintAsync(a, PrintStatus.Printing, "Gridfinity");
        await _factory.DeliverPendingAsync(Ct);

        var body = Body(Assert.Single(_factory.Receiver.Received(a.Host)));
        Assert.Equal("print.started", body.GetProperty("event").GetString());
        var printData = body.GetProperty("data").GetProperty("print");
        Assert.Equal(print.Id, printData.GetProperty("id").GetInt64());
        Assert.Equal("Printing", printData.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, printData.GetProperty("finishedAt").ValueKind);
    }

    [Fact]
    public async Task CreatingAFinishedPrint_SendsNoStart()
    {
        var a = await ArrangeAsync(events: ["print.started"]);

        await CreatePrintAsync(a, PrintStatus.Success);
        await _factory.DeliverPendingAsync(Ct);

        Assert.Empty(_factory.Receiver.Received(a.Host));
    }

    [Fact]
    public async Task ReplayingAConnectorCreate_SendsOneStart()
    {
        var a = await ArrangeAsync(events: ["print.started"]);
        var externalId = Guid.NewGuid().ToString("N");

        for (var i = 0; i < 2; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints")
            {
                Content = JsonContent.Create(new AddPrintDTO
                {
                    Title = "Bridge print",
                    PrinterId = a.PrinterId,
                    Status = PrintStatus.Printing,
                    ExternalSource = "moonraker",
                    ExternalId = externalId,
                    FilamentUsage = [],
                }),
            };
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, a.User.OAuthUserId);
            Assert.True((await _client.SendAsync(request, Ct)).IsSuccessStatusCode);
        }
        await _factory.DeliverPendingAsync(Ct);

        Assert.Single(_factory.Receiver.Received(a.Host));
    }

    [Fact]
    public async Task AConnectorReplay_FromPrintingToFinished_SendsFinishedAndNotifies_ExactlyOnce()
    {
        var a = await ArrangeAsync(events: ["print.finished"]);
        var externalId = Guid.NewGuid().ToString("N");

        foreach (var status in new[] { PrintStatus.Printing, PrintStatus.Success, PrintStatus.Success })
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints")
            {
                Content = JsonContent.Create(new AddPrintDTO
                {
                    Title = "Bridge finish",
                    PrinterId = a.PrinterId,
                    Status = status,
                    ExternalSource = "moonraker",
                    ExternalId = externalId,
                    FilamentUsage = [],
                }),
            };
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, a.User.OAuthUserId);
            Assert.True((await _client.SendAsync(request, Ct)).IsSuccessStatusCode);
        }
        await _factory.DeliverPendingAsync(Ct);

        var body = Body(Assert.Single(_factory.Receiver.Received(a.Host)));
        Assert.Equal("print.finished", body.GetProperty("event").GetString());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == a.User.Id && n.Type == NotificationType.PrintCompleted, Ct));
    }

    [Fact]
    public async Task AConnectorReplay_FromPendingToPrinting_SendsStarted()
    {
        var a = await ArrangeAsync(events: ["print.started"]);
        var externalId = Guid.NewGuid().ToString("N");

        foreach (var status in new[] { PrintStatus.Pending, PrintStatus.Printing })
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/Prints")
            {
                Content = JsonContent.Create(new AddPrintDTO
                {
                    Title = "Queued print",
                    PrinterId = a.PrinterId,
                    Status = status,
                    ExternalSource = "moonraker",
                    ExternalId = externalId,
                    FilamentUsage = [],
                }),
            };
            request.Headers.Add(TestAuthHandler.TestUserIdHeader, a.User.OAuthUserId);
            Assert.True((await _client.SendAsync(request, Ct)).IsSuccessStatusCode);
            await _factory.DeliverPendingAsync(Ct);
            Assert.Equal(status == PrintStatus.Pending ? 0 : 1, _factory.Receiver.Received(a.Host).Count);
        }
    }

    [Fact]
    public async Task ThePrintEventPipeline_SendsStarted_OnceForARedeliveredStart()
    {
        var a = await ArrangeAsync(events: ["print.started"]);
        using (var scope = _factory.Services.CreateScope())
        {
            var events = scope.ServiceProvider.GetRequiredService<IPrintEventService>();
            await events.Started(StartedEvent(a, "pipeline-start"));
            await events.Started(StartedEvent(a, "pipeline-start"));
        }

        await _factory.DeliverPendingAsync(Ct);

        var body = Body(Assert.Single(_factory.Receiver.Received(a.Host)));
        Assert.Equal("print.started", body.GetProperty("event").GetString());
    }

    [Fact]
    public async Task AnMcpCreatedPrintingPrint_SendsStarted()
    {
        var a = await ArrangeAsync(events: ["print.started"]);
        using (var scope = _factory.Services.CreateScope())
        {
            var prints = scope.ServiceProvider.GetRequiredService<IPrintService>();
            await prints.CreatePrintForMcp(a.User.Id, "Agent print", a.PrinterId, PrintStatus.Printing,
                DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null,
                [], Guid.NewGuid().ToString("N"), Ct);
        }

        await _factory.DeliverPendingAsync(Ct);

        var body = Body(Assert.Single(_factory.Receiver.Received(a.Host)));
        Assert.Equal("Agent print", body.GetProperty("data").GetProperty("print").GetProperty("title").GetString());
    }

    private static PrintStartedEvent StartedEvent(Arranged a, string name) => new()
    {
        UserId = a.User.Id,
        PrinterId = a.PrinterId,
        Source = PrintSource.Moonraker,
        ExternalSource = "moonraker-notifier",
        ExternalId = $"{name}-{a.User.Id}",
        Title = name,
        FileName = $"{name}.gcode",
        StartDate = DateTimeOffset.UtcNow,
        Usage = [],
    };

    #endregion

    #region Who receives it

    [Fact]
    public async Task ADisabledSubscription_ReceivesNothing()
    {
        var a = await ArrangeAsync();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/Webhooks/{a.Subscription.Id}")
        {
            Content = JsonContent.Create(new { url = a.Subscription.Url, enabled = false }),
        };
        request.Headers.Add(TestAuthHandler.TestUserIdHeader, a.User.OAuthUserId);
        Assert.True((await _client.SendAsync(request, Ct)).IsSuccessStatusCode);

        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);

        Assert.Empty(_factory.Receiver.Received(a.Host));
    }

    [Fact]
    public async Task AnotherUsersPrint_NeverReachesThisUsersWebhook()
    {
        var mine = await ArrangeAsync();
        var theirs = await ArrangeAsync();

        await CreatePrintAsync(theirs, PrintStatus.Printing, "Their print");
        await _factory.DeliverPendingAsync(Ct);

        Assert.Empty(_factory.Receiver.Received(mine.Host));
        Assert.Single(_factory.Receiver.Received(theirs.Host));
    }

    [Fact]
    public async Task EverySubscribedWebhook_GetsTheSameEvent()
    {
        var a = await ArrangeAsync(pro: true);
        var secondHost = $"second-{Guid.NewGuid():N}.example.test";
        await SubscribeAsync(a.User, secondHost);

        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);

        var first = Assert.Single(_factory.Receiver.Received(a.Host));
        var second = Assert.Single(_factory.Receiver.Received(secondHost));
        Assert.Equal(first.Body, second.Body);
        Assert.NotEqual(first.Headers["X-PrintLog-Signature"], second.Headers["X-PrintLog-Signature"]);
    }

    #endregion

    #region Retries and failures

    [Fact]
    public async Task AServerError_IsRetried_WithTheSameEvent()
    {
        var a = await ArrangeAsync();
        _factory.Receiver.Respond(a.Host, HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway);

        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);

        var received = _factory.Receiver.Received(a.Host);
        Assert.Equal(3, received.Count);
        Assert.Single(received.Select(r => r.Headers["X-PrintLog-Event-Id"]).Distinct());
        Assert.Equal(["1", "2", "3"], received.Select(r => r.Headers["X-PrintLog-Delivery-Attempt"]));
        var stored = await StoredAsync(a.Subscription.Id);
        Assert.Equal(0, stored.ConsecutiveFailures);
        Assert.Equal("success", stored.LastDeliveryResult);
        Assert.Equal(200, stored.LastStatusCode);
        Assert.NotNull(stored.LastDeliveryAt);
    }

    [Fact]
    public async Task ATimeoutOrConnectionFailure_IsRetried()
    {
        var a = await ArrangeAsync();
        _factory.Receiver.Fail(a.Host, 1, () => new TaskCanceledException("timed out"));
        _factory.Receiver.Fail(a.Host, 1, () => new HttpRequestException("connection refused"));

        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);

        Assert.Equal(3, _factory.Receiver.Received(a.Host).Count);
        Assert.Equal("success", (await StoredAsync(a.Subscription.Id)).LastDeliveryResult);
    }

    [Fact]
    public async Task AClientError_IsNotRetried_AndCountsAsAFailure()
    {
        var a = await ArrangeAsync();
        _factory.Receiver.Respond(a.Host, HttpStatusCode.NotFound);

        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);

        Assert.Single(_factory.Receiver.Received(a.Host));
        var stored = await StoredAsync(a.Subscription.Id);
        Assert.Equal(1, stored.ConsecutiveFailures);
        Assert.Equal(404, stored.LastStatusCode);
        Assert.Equal("http_error", stored.LastDeliveryResult);
        Assert.True(stored.Enabled);
    }

    [Fact]
    public async Task GivingUpAfterTheRetries_CountsOneFailure()
    {
        var a = await ArrangeAsync();
        _factory.Receiver.Respond(a.Host, Enumerable.Repeat(HttpStatusCode.ServiceUnavailable, 4).ToArray());

        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);

        Assert.Equal(4, _factory.Receiver.Received(a.Host).Count);
        var stored = await StoredAsync(a.Subscription.Id);
        Assert.Equal(1, stored.ConsecutiveFailures);
        Assert.Equal(503, stored.LastStatusCode);
    }

    [Fact]
    public async Task ASuccess_ResetsTheFailureCount()
    {
        var a = await ArrangeAsync();
        _factory.Receiver.Respond(a.Host, HttpStatusCode.Gone, HttpStatusCode.Gone);
        await CreatePrintAsync(a, PrintStatus.Printing);
        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);
        Assert.Equal(2, (await StoredAsync(a.Subscription.Id)).ConsecutiveFailures);

        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);

        Assert.Equal(0, (await StoredAsync(a.Subscription.Id)).ConsecutiveFailures);
    }

    [Fact]
    public async Task RepeatedFailures_TurnTheWebhookOff_AndTellTheUserOnce()
    {
        // The factory sets MaxConsecutiveFailures to 3.
        var a = await ArrangeAsync();
        _factory.Receiver.Respond(a.Host, Enumerable.Repeat(HttpStatusCode.NotFound, 3).ToArray());
        for (var i = 0; i < 3; i++)
        {
            await CreatePrintAsync(a, PrintStatus.Printing);
        }
        await _factory.DeliverPendingAsync(Ct);

        var stored = await StoredAsync(a.Subscription.Id);
        Assert.False(stored.Enabled);
        Assert.NotNull(stored.AutoDisabledAt);
        Assert.Equal(3, stored.ConsecutiveFailures);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            // Filtered by title: the user's first prints also earn achievement notifications.
            var notification = Assert.Single(await db.Notifications
                .Where(n => n.UserId == a.User.Id && n.Title == "Webhook turned off").ToListAsync(Ct));
            Assert.Equal(NotificationType.SystemAnnouncement, notification.Type);
            Assert.Contains(a.Host, notification.Message);
            // The path of a Home Assistant webhook URL is its secret.
            Assert.DoesNotContain("secret-hook-id", notification.Message);
            Assert.DoesNotContain("secret-hook-id", notification.Title);
        }

        // Off means off: the next print sends nothing.
        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);
        Assert.Equal(3, _factory.Receiver.Received(a.Host).Count);
    }

    [Fact]
    public async Task ADeletedPrint_IsSkipped()
    {
        var a = await ArrangeAsync();
        var print = await CreatePrintAsync(a, PrintStatus.Printing);
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PrintLogContext>().Prints.Where(p => p.Id == print.Id).ExecuteDeleteAsync(Ct);
        }

        await _factory.DeliverPendingAsync(Ct);

        Assert.Empty(_factory.Receiver.Received(a.Host));
    }

    [Fact]
    public async Task AnUndecryptableSecret_IsRecorded_AndNothingIsSent()
    {
        var a = await ArrangeAsync();
        await CorruptSecretAsync(a.Subscription.Id);

        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);

        Assert.Empty(_factory.Receiver.Received(a.Host));
        var stored = await StoredAsync(a.Subscription.Id);
        Assert.Equal("secret_unavailable", stored.LastDeliveryResult);
        Assert.Equal(1, stored.ConsecutiveFailures);
    }

    [Fact]
    public async Task ARetry_Stops_WhenTheWebhookWasTurnedOffMeanwhile()
    {
        var a = await ArrangeAsync();
        _factory.Receiver.Respond(a.Host, () =>
        {
            Update(a.Subscription.Id, w => w.Enabled = false);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);

        Assert.Single(_factory.Receiver.Received(a.Host));
    }

    [Fact]
    public async Task ARetry_Stops_WhenTheUrlChangedMeanwhile()
    {
        var a = await ArrangeAsync();
        _factory.Receiver.Respond(a.Host, () =>
        {
            Update(a.Subscription.Id, w => w.Url = "https://elsewhere.example.test/api/webhook/new");
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);

        Assert.Single(_factory.Receiver.Received(a.Host));
        Assert.Empty(_factory.Receiver.Received("elsewhere.example.test"));
    }

    [Fact]
    public async Task ARetry_Stops_WhenTheSecretWasRotatedMeanwhile()
    {
        var a = await ArrangeAsync();
        _factory.Receiver.Respond(a.Host, () =>
        {
            Update(a.Subscription.Id, w => w.EncryptedSecret = "v1.rotated.AAAA");
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);

        Assert.Single(_factory.Receiver.Received(a.Host));
    }

    [Fact]
    public async Task AnOpenCircuit_SkipsTheDelivery_UntilTheCooldownPasses()
    {
        var a = await ArrangeAsync();
        Update(a.Subscription.Id, w =>
        {
            w.ConsecutiveFailures = 5;
            w.LastDeliveryAt = DateTime.UtcNow;
        });

        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);

        Assert.Empty(_factory.Receiver.Received(a.Host));
        Assert.Equal(5, (await StoredAsync(a.Subscription.Id)).ConsecutiveFailures);
    }

    [Fact]
    public async Task AfterTheCooldown_OneProbeIsSent_WithoutRetries()
    {
        var a = await ArrangeAsync();
        Update(a.Subscription.Id, w =>
        {
            w.ConsecutiveFailures = 5;
            w.LastDeliveryAt = DateTime.UtcNow.AddHours(-1);
        });
        _factory.Receiver.Respond(a.Host, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);

        await CreatePrintAsync(a, PrintStatus.Printing);
        await _factory.DeliverPendingAsync(Ct);

        Assert.Single(_factory.Receiver.Received(a.Host));
        Assert.Equal(6, (await StoredAsync(a.Subscription.Id)).ConsecutiveFailures);
    }

    [Fact]
    public async Task AfterTheCooldown_OnlyOneEventProbes()
    {
        var a = await ArrangeAsync();
        Update(a.Subscription.Id, w =>
        {
            w.ConsecutiveFailures = 5;
            w.LastDeliveryAt = DateTime.UtcNow.AddHours(-1);
        });
        _factory.Receiver.Respond(a.Host, HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);

        await CreatePrintAsync(a, PrintStatus.Printing, "First");
        await CreatePrintAsync(a, PrintStatus.Printing, "Second");
        var deliveries = await PrepareQueuedAsync();

        // Both events were prepared before either probe ran: the claim, not the timing, decides.
        Assert.Single(deliveries);
        await AttemptAllAsync(deliveries);
        Assert.Single(_factory.Receiver.Received(a.Host));
    }

    [Fact]
    public async Task AFirstAttempt_IsDropped_WhenTheWebhookWasDeletedWhileItWaited()
    {
        var a = await ArrangeAsync();
        await CreatePrintAsync(a, PrintStatus.Printing);
        var deliveries = await PrepareQueuedAsync();
        Assert.Single(deliveries);

        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/Webhooks/{a.Subscription.Id}");
        delete.Headers.Add(TestAuthHandler.TestUserIdHeader, a.User.OAuthUserId);
        Assert.True((await _client.SendAsync(delete, Ct)).IsSuccessStatusCode);
        await AttemptAllAsync(deliveries);

        Assert.Empty(_factory.Receiver.Received(a.Host));
    }

    [Fact]
    public async Task NothingIsSent_AfterTheAccountIsDeleted()
    {
        var a = await ArrangeAsync();
        await CreatePrintAsync(a, PrintStatus.Printing);
        var deliveries = await PrepareQueuedAsync();
        Assert.Single(deliveries);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
            var user = await db.Users.SingleAsync(u => u.Id == a.User.Id, Ct);
            await scope.ServiceProvider.GetRequiredService<IUserDeletionService>().DeleteAllDataForUser(user);
        }
        await AttemptAllAsync(deliveries);

        Assert.Empty(_factory.Receiver.Received(a.Host));
    }

    [Fact]
    public async Task AFirstAttempt_IsSkipped_WhenTheCircuitOpenedWhileItWaited()
    {
        var a = await ArrangeAsync();
        await CreatePrintAsync(a, PrintStatus.Printing);
        var deliveries = await PrepareQueuedAsync();
        Update(a.Subscription.Id, w =>
        {
            w.ConsecutiveFailures = 3;
            w.LastDeliveryAt = DateTime.UtcNow;
        });

        await AttemptAllAsync(deliveries);

        Assert.Empty(_factory.Receiver.Received(a.Host));
        Assert.Equal(3, (await StoredAsync(a.Subscription.Id)).ConsecutiveFailures);
    }

    /// <summary>Fans out everything queued, without attempting any of it yet.</summary>
    private async Task<List<WebhookDelivery>> PrepareQueuedAsync()
    {
        var queue = _factory.Services.GetRequiredService<WebhookEventQueue>();
        var deliveries = new List<WebhookDelivery>();
        while (queue.Reader.TryRead(out var webhookEvent))
        {
            using var scope = _factory.Services.CreateScope();
            deliveries.AddRange(await scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>().PrepareAsync(webhookEvent, Ct));
        }
        return deliveries;
    }

    private async Task AttemptAllAsync(IEnumerable<WebhookDelivery> deliveries)
    {
        foreach (var first in deliveries)
        {
            for (WebhookDelivery? delivery = first; delivery is not null;)
            {
                using var scope = _factory.Services.CreateScope();
                delivery = (await scope.ServiceProvider.GetRequiredService<WebhookDeliveryProcessor>().AttemptAsync(delivery, Ct)).Next;
            }
        }
    }

    private void Update(Guid id, Action<WebhookSubscription> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
        var row = db.WebhookSubscriptions.Single(w => w.Id == id);
        change(row);
        db.SaveChanges();
    }

    private Task CorruptSecretAsync(Guid id)
    {
        // A value encrypted for another row: well formed, but it cannot be decrypted for this one.
        var protector = _factory.Services.GetRequiredService<WebhookSecretProtector>();
        var foreign = protector.Protect(Guid.NewGuid(), "whsec_other");
        Update(id, w => w.EncryptedSecret = foreign);
        return Task.CompletedTask;
    }

    #endregion
}

/// <summary>Private addresses open only in Development, whatever the setting says elsewhere.</summary>
public class WebhookPrivateAddressSwitchTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "PrintLogApi";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Development", false, false)]
    [InlineData("Production", true, false)]
    [InlineData("Staging", true, false)]
    [InlineData("E2ETesting", true, false)]
    [InlineData("IntegrationTesting", true, false)]
    public void OnlyDevelopment_HonorsTheSwitch(string environment, bool requested, bool expected)
    {
        Assert.Equal(expected, WebhookServiceCollectionExtensions.AllowPrivateAddresses(requested, new Env(environment)));
    }

    [Fact]
    public async Task OutsideDevelopment_TheSettingDoesNotOpenPrivateAddresses()
    {
        await using var factory = new WebhookTestFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Webhooks:AllowPrivateAddresses"] = "true",
            })));

        var guard = factory.Services.GetRequiredService<WebhookAddressGuard>();

        Assert.False(guard.AllowPrivateAddresses);
        Assert.Equal(WebhookUrlStatus.Blocked,
            (await guard.ValidateAsync("http://192.168.1.20:8123/api/webhook/x", TestContext.Current.CancellationToken)).Status);
    }
}
