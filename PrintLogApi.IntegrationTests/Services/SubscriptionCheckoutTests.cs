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
    }
}
