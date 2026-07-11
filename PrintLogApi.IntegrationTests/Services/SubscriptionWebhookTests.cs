using System.Linq;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Services;
using PrintLogApi.Services.Billing;
using Stripe;
using Stripe.Checkout;
using Xunit;
using Subscription = PrintLogApi.Models.Subscription;

namespace PrintLogApi.IntegrationTests.Services
{
    public class SubscriptionWebhookTests
    {
        private static Event CompletedEvent(string sessionId, string subscriptionId, string customerId, long userId)
        {
            var session = new Session
            {
                Id = sessionId,
                SubscriptionId = subscriptionId,
                CustomerId = customerId,
                Metadata = new System.Collections.Generic.Dictionary<string, string> { { "userId", userId.ToString() } }
            };
            return new Event
            {
                Type = EventTypes.CheckoutSessionCompleted,
                Data = new EventData { Object = session }
            };
        }

        // Genuine red-green: the CURRENT handler never touches pending fields, so a
        // MATCHING completion leaves them set -> this assertion fails before Task 6.
        [Fact]
        public async Task Completion_MatchingPendingSession_ClearsPendingFields()
        {
            var fake = new FakeStripeGateway();
            fake.SubscriptionsById["sub_ok"] = new StripeSubscriptionInfo { Id = "sub_ok", Status = "active", PriceId = "price_x" };
            fake.QueuedWebhookEvent = CompletedEvent("cs_match", "sub_ok", "cus_1", IntegrationTestSeeder.TestUserId);
            await using var factory = new CustomWebApplicationFactory().WithStripeGateway(fake);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                db.Subscriptions.Add(new Subscription
                {
                    UserId = IntegrationTestSeeder.TestUserId,
                    StripeCustomerId = "cus_1",
                    Status = SubscriptionStatus.None,
                    Plan = SubscriptionPlan.Free,
                    PendingCheckoutSessionId = "cs_match",
                    PendingCheckoutSessionUrl = "https://checkout.stripe.test/match",
                    PendingCheckoutIdempotencyKey = "attempt-match",
                    PendingCheckoutPlanId = "pro_monthly",
                    PendingCheckoutExpiresAt = System.DateTimeOffset.UtcNow.AddMinutes(9),
                    CreatedById = IntegrationTestSeeder.TestUserId,
                    UpdatedById = IntegrationTestSeeder.TestUserId
                });
                await db.SaveChangesAsync();
            }

            using (var scope = factory.Services.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
                await svc.HandleStripeWebhook("{}", "sig");
            }

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                var sub = db.Subscriptions.Single(s => s.UserId == IntegrationTestSeeder.TestUserId);
                Assert.Equal("sub_ok", sub.StripeSubscriptionId);
                Assert.Null(sub.PendingCheckoutSessionId);         // cleared
                Assert.Null(sub.PendingCheckoutIdempotencyKey);    // cleared
            }
        }

        // Regression guard (may already pass pre-Task 6, since the current handler
        // ignores pending fields): a NON-matching completion must not wipe a newer attempt.
        [Fact]
        public async Task Completion_ForNonCurrentPendingSession_DoesNotClearNewerAttempt()
        {
            var fake = new FakeStripeGateway();
            fake.SubscriptionsById["sub_old"] = new StripeSubscriptionInfo { Id = "sub_old", Status = "active", PriceId = "price_x" };
            fake.QueuedWebhookEvent = CompletedEvent("cs_old", "sub_old", "cus_1", IntegrationTestSeeder.TestUserId);
            await using var factory = new CustomWebApplicationFactory().WithStripeGateway(fake);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                db.Subscriptions.Add(new Subscription
                {
                    UserId = IntegrationTestSeeder.TestUserId,
                    StripeCustomerId = "cus_1",
                    Status = SubscriptionStatus.None,
                    Plan = SubscriptionPlan.Free,
                    PendingCheckoutSessionId = "cs_newer",
                    PendingCheckoutSessionUrl = "https://checkout.stripe.test/newer",
                    PendingCheckoutIdempotencyKey = "attempt-newer",
                    PendingCheckoutPlanId = "pro_annual",
                    PendingCheckoutExpiresAt = System.DateTimeOffset.UtcNow.AddHours(12),
                    CreatedById = IntegrationTestSeeder.TestUserId,
                    UpdatedById = IntegrationTestSeeder.TestUserId
                });
                await db.SaveChangesAsync();
            }

            using (var scope = factory.Services.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
                await svc.HandleStripeWebhook("{}", "sig");
            }

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                var sub = db.Subscriptions.Single(s => s.UserId == IntegrationTestSeeder.TestUserId);
                Assert.Equal("cs_newer", sub.PendingCheckoutSessionId); // newer attempt preserved
            }
        }

        [Fact]
        public async Task Completion_WithDifferentLiveSubscription_DoesNotOverwrite_AndEmitsDuplicateTelemetry()
        {
            var fake = new FakeStripeGateway();
            fake.SubscriptionsById["sub_new"] = new StripeSubscriptionInfo { Id = "sub_new", Status = "active", PriceId = "price_x" };
            fake.QueuedWebhookEvent = CompletedEvent("cs_new", "sub_new", "cus_1", IntegrationTestSeeder.TestUserId);
            await using var factory = new CustomWebApplicationFactory().WithStripeGateway(fake);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                db.Subscriptions.Add(new Subscription
                {
                    UserId = IntegrationTestSeeder.TestUserId,
                    StripeCustomerId = "cus_1",
                    StripeSubscriptionId = "sub_original",
                    Status = SubscriptionStatus.Active, // live -> genuinely a different subscription
                    Plan = SubscriptionPlan.ProMonthly,
                    CreatedById = IntegrationTestSeeder.TestUserId,
                    UpdatedById = IntegrationTestSeeder.TestUserId
                });
                await db.SaveChangesAsync();
            }

            using (var scope = factory.Services.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
                await svc.HandleStripeWebhook("{}", "sig");
            }

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                var sub = db.Subscriptions.Single(s => s.UserId == IntegrationTestSeeder.TestUserId);
                Assert.Equal("sub_original", sub.StripeSubscriptionId); // not overwritten
            }

            var dup = factory.TelemetryChannel.Items.OfType<EventTelemetry>()
                .SingleOrDefault(e => e.Name == "Subscription_DuplicateActiveDetected");
            Assert.NotNull(dup);
            Assert.Equal("sub_original", dup.Properties["existingSubscriptionId"]); // reloaded from DB, not null
            Assert.Equal("sub_new", dup.Properties["incomingSubscriptionId"]);
        }

        // Review finding 1 regression guard: a CANCELED row still holds sub_old; a
        // completion for sub_new must ACTIVATE (the non-live clause permits replacement)
        // and NOT log a false duplicate. A naive `id==null || id==incoming` predicate
        // would fail this.
        [Fact]
        public async Task Completion_ReSubscribeAfterCancellation_Activates()
        {
            var fake = new FakeStripeGateway();
            fake.SubscriptionsById["sub_new"] = new StripeSubscriptionInfo { Id = "sub_new", Status = "active", PriceId = "price_x" };
            fake.QueuedWebhookEvent = CompletedEvent("cs_new", "sub_new", "cus_1", IntegrationTestSeeder.TestUserId);
            await using var factory = new CustomWebApplicationFactory().WithStripeGateway(fake);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                db.Subscriptions.Add(new Subscription
                {
                    UserId = IntegrationTestSeeder.TestUserId,
                    StripeCustomerId = "cus_1",
                    StripeSubscriptionId = "sub_old", // retained after cancellation
                    Status = SubscriptionStatus.Canceled,
                    Plan = SubscriptionPlan.Free,
                    CreatedById = IntegrationTestSeeder.TestUserId,
                    UpdatedById = IntegrationTestSeeder.TestUserId
                });
                await db.SaveChangesAsync();
            }

            using (var scope = factory.Services.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
                await svc.HandleStripeWebhook("{}", "sig");
            }

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                var sub = db.Subscriptions.Single(s => s.UserId == IntegrationTestSeeder.TestUserId);
                Assert.Equal("sub_new", sub.StripeSubscriptionId);      // activated, not blocked
                Assert.Equal(SubscriptionStatus.Active, sub.Status);
            }

            Assert.DoesNotContain(
                factory.TelemetryChannel.Items.OfType<EventTelemetry>(),
                e => e.Name == "Subscription_DuplicateActiveDetected");
        }

        // A delayed/replayed completion for a subscription that is no longer active records the
        // truthful status but must NOT announce an activation.
        [Fact]
        public async Task Completion_WithNonActiveStripeStatus_DoesNotAnnounceActivation()
        {
            var fake = new FakeStripeGateway();
            fake.SubscriptionsById["sub_canceled"] = new StripeSubscriptionInfo { Id = "sub_canceled", Status = "canceled", PriceId = "price_x" };
            fake.QueuedWebhookEvent = CompletedEvent("cs_x", "sub_canceled", "cus_1", IntegrationTestSeeder.TestUserId);
            await using var factory = new CustomWebApplicationFactory().WithStripeGateway(fake);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                db.Subscriptions.Add(new Subscription
                {
                    UserId = IntegrationTestSeeder.TestUserId,
                    StripeCustomerId = "cus_1",
                    Status = SubscriptionStatus.None,
                    Plan = SubscriptionPlan.Free,
                    CreatedById = IntegrationTestSeeder.TestUserId,
                    UpdatedById = IntegrationTestSeeder.TestUserId
                });
                await db.SaveChangesAsync();
            }

            using (var scope = factory.Services.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
                await svc.HandleStripeWebhook("{}", "sig");
            }

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                var sub = db.Subscriptions.Single(s => s.UserId == IntegrationTestSeeder.TestUserId);
                Assert.Equal(SubscriptionStatus.Canceled, sub.Status); // truthful status recorded
            }

            Assert.DoesNotContain(
                factory.TelemetryChannel.Items.OfType<EventTelemetry>(),
                e => e.Name == "Subscription_Activated");
        }
    }
}
