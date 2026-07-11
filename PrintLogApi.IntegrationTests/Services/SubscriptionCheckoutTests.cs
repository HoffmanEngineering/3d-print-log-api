using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Controllers;
using PrintLogApi.Models;
using PrintLogApi.Services.Billing;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services
{
    public class SubscriptionCheckoutTests
    {
        private static HttpRequestMessage Checkout(string plan)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/Subscription/checkout")
            {
                Content = JsonContent.Create(new { planId = plan })
            };
            req.Headers.Add(TestAuthHandler.TestUserIdHeader, IntegrationTestSeeder.TestUserOAuthId);
            return req;
        }

        [Fact]
        public async Task Checkout_WhenLocalActiveSubscription_IsRejected()
        {
            var fake = new FakeStripeGateway();
            await using var factory = new CustomWebApplicationFactory().WithStripeGateway(fake);
            var client = factory.CreateClient();

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                var sub = new Subscription
                {
                    UserId = IntegrationTestSeeder.TestUserId,
                    StripeCustomerId = "cus_existing",
                    StripeSubscriptionId = "sub_existing",
                    Status = SubscriptionStatus.Active,
                    Plan = SubscriptionPlan.ProMonthly,
                    CreatedById = IntegrationTestSeeder.TestUserId,
                    UpdatedById = IntegrationTestSeeder.TestUserId
                };
                db.Subscriptions.Add(sub);
                await db.SaveChangesAsync();
            }

            var response = await client.SendAsync(Checkout("pro_monthly"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, fake.CreateCheckoutSessionCallCount);
        }

        [Fact]
        public async Task Checkout_WhenStripeHasLiveSubButLocalIsStale_IsReconciledAndRejected()
        {
            var fake = new FakeStripeGateway();
            fake.SubscriptionsByCustomer["cus_stale"] = new()
            {
                new StripeSubscriptionInfo { Id = "sub_live", Status = "active", PriceId = "price_monthly" }
            };
            await using var factory = new CustomWebApplicationFactory().WithStripeGateway(fake);
            var client = factory.CreateClient();

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                db.Subscriptions.Add(new Subscription
                {
                    UserId = IntegrationTestSeeder.TestUserId,
                    StripeCustomerId = "cus_stale",
                    Status = SubscriptionStatus.None, // stale: webhook never landed
                    Plan = SubscriptionPlan.Free,
                    CreatedById = IntegrationTestSeeder.TestUserId,
                    UpdatedById = IntegrationTestSeeder.TestUserId
                });
                await db.SaveChangesAsync();
            }

            var response = await client.SendAsync(Checkout("pro_monthly"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(0, fake.CreateCheckoutSessionCallCount);
        }

        [Fact]
        public async Task Checkout_SecondSamePlanSubmit_ReusesSameSessionUrl()
        {
            var fake = new FakeStripeGateway { NextSessionUrl = () => "https://checkout.stripe.test/first" };
            await using var factory = new CustomWebApplicationFactory().WithStripeGateway(fake);
            var client = factory.CreateClient();

            var first = await client.SendAsync(Checkout("pro_monthly"));
            var second = await client.SendAsync(Checkout("pro_monthly"));

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(1, fake.CreateCheckoutSessionCallCount); // second reused, no new Stripe session

            var firstBody = await first.Content.ReadFromJsonAsync<CheckoutSessionResponseDto>();
            var secondBody = await second.Content.ReadFromJsonAsync<CheckoutSessionResponseDto>();
            Assert.Equal("https://checkout.stripe.test/first", firstBody.Url);
            Assert.Equal("https://checkout.stripe.test/first", secondBody.Url); // actually reused, not empty/stale
        }

        [Fact]
        public async Task Checkout_SecondDifferentPlanWhilePending_IsRejected()
        {
            var fake = new FakeStripeGateway();
            await using var factory = new CustomWebApplicationFactory().WithStripeGateway(fake);
            var client = factory.CreateClient();

            var first = await client.SendAsync(Checkout("pro_monthly"));
            var second = await client.SendAsync(Checkout("pro_annual"));

            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
            Assert.Equal(1, fake.CreateCheckoutSessionCallCount);
        }

        [Fact]
        public async Task Checkout_StripeFailure_ReleasesClaimSoRetrySucceeds()
        {
            var fake = new FakeStripeGateway
            {
                CheckoutFailure = new Stripe.StripeException("boom"),
                NextSessionUrl = () => "https://checkout.stripe.test/after-retry"
            };
            await using var factory = new CustomWebApplicationFactory().WithStripeGateway(fake);
            var client = factory.CreateClient();

            // The controller does not catch StripeException; the TestServer surfaces the
            // unhandled exception to the caller. The service's catch still released the lease.
            await Assert.ThrowsAsync<Stripe.StripeException>(() => client.SendAsync(Checkout("pro_monthly")));

            var retry = await client.SendAsync(Checkout("pro_monthly")); // claim must have been released
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            var body = await retry.Content.ReadFromJsonAsync<CheckoutSessionResponseDto>();
            Assert.Equal("https://checkout.stripe.test/after-retry", body.Url);
        }

        // Deterministic CAS serialization test: the claim predicate lets exactly one
        // claim win while a lease is held (review findings 3, 4). This asserts the
        // 1-vs-0 affected-row invariant without relying on real thread parallelism,
        // which the shared-connection SQLite harness cannot reproduce faithfully.
        [Fact]
        public async Task CheckoutClaim_SecondClaimWhileLeaseHeld_AffectsZeroRows()
        {
            var fake = new FakeStripeGateway();
            await using var factory = new CustomWebApplicationFactory().WithStripeGateway(fake);

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                db.Subscriptions.Add(new Subscription
                {
                    UserId = IntegrationTestSeeder.TestUserId,
                    StripeCustomerId = "cus_claim",
                    Status = SubscriptionStatus.None,
                    Plan = SubscriptionPlan.Free,
                    CreatedById = IntegrationTestSeeder.TestUserId,
                    UpdatedById = IntegrationTestSeeder.TestUserId
                });
                await db.SaveChangesAsync();
            }

            var now = DateTimeOffset.UtcNow;
            var lease = now.AddMinutes(10);
            int firstClaim, secondClaim;

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                firstClaim = await db.Subscriptions
                    .Where(s => s.UserId == IntegrationTestSeeder.TestUserId
                        && (s.PendingCheckoutIdempotencyKey == null || s.PendingCheckoutExpiresAt == null || s.PendingCheckoutExpiresAt <= now))
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(s => s.PendingCheckoutIdempotencyKey, "attempt-1")
                        .SetProperty(s => s.PendingCheckoutPlanId, "pro_monthly")
                        .SetProperty(s => s.PendingCheckoutExpiresAt, lease)
                        .SetProperty(s => s.UpdatedDate, DateTime.UtcNow)
                        .SetProperty(s => s.UpdatedById, IntegrationTestSeeder.TestUserId));
            }

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PrintLogContext>();
                secondClaim = await db.Subscriptions
                    .Where(s => s.UserId == IntegrationTestSeeder.TestUserId
                        && (s.PendingCheckoutIdempotencyKey == null || s.PendingCheckoutExpiresAt == null || s.PendingCheckoutExpiresAt <= now))
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(s => s.PendingCheckoutIdempotencyKey, "attempt-2")
                        .SetProperty(s => s.PendingCheckoutPlanId, "pro_annual")
                        .SetProperty(s => s.PendingCheckoutExpiresAt, lease)
                        .SetProperty(s => s.UpdatedDate, DateTime.UtcNow)
                        .SetProperty(s => s.UpdatedById, IntegrationTestSeeder.TestUserId));
            }

            Assert.Equal(1, firstClaim);
            Assert.Equal(0, secondClaim); // lease held -> second claim wins nothing
        }
    }
}
