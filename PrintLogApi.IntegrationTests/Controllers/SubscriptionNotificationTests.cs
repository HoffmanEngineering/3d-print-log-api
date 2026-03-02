using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PrintLogApi.Models;
using PrintLogApi.Services;
using Xunit;

namespace PrintLogApi.IntegrationTests.Controllers
{
    public class SubscriptionNotificationTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;

        public SubscriptionNotificationTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task CreateSubscriptionActivatedNotification_CreatesCorrectNotification()
        {
            using var scope = _factory.Services.CreateScope();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

            var notification = await notificationService.CreateSubscriptionActivatedNotification(
                IntegrationTestSeeder.TestUserId, "Pro Monthly");

            Assert.NotNull(notification);
            Assert.Equal(NotificationType.SubscriptionActivated, notification.Type);
            Assert.Contains("Pro", notification.Title, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal("/settings/subscription", notification.ActionUrl);
        }

        [Fact]
        public async Task CreateSubscriptionPaymentFailedNotification_CreatesCorrectNotification()
        {
            using var scope = _factory.Services.CreateScope();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

            var notification = await notificationService.CreateSubscriptionPaymentFailedNotification(
                IntegrationTestSeeder.TestUserId);

            Assert.NotNull(notification);
            Assert.Equal(NotificationType.SubscriptionPaymentFailed, notification.Type);
            Assert.Equal("/settings/subscription", notification.ActionUrl);
        }

        [Fact]
        public async Task CreateSubscriptionCanceledNotification_CreatesCorrectNotification()
        {
            using var scope = _factory.Services.CreateScope();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

            var notification = await notificationService.CreateSubscriptionCanceledNotification(
                IntegrationTestSeeder.TestUserId);

            Assert.NotNull(notification);
            Assert.Equal(NotificationType.SubscriptionCanceled, notification.Type);
            Assert.Equal("/settings/subscription", notification.ActionUrl);
        }
    }
}
