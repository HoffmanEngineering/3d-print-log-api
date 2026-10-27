using PrintLogApi.Services.Push;
using Xunit;

namespace PrintLogApi.IntegrationTests.Services;

/// <summary>
/// The payload FirebaseFcmClient sends, built without a Firebase app. What the iOS app's
/// push plugin (firebasex) reads from it is a contract with that plugin, not a formatting choice.
/// </summary>
public class FcmMessagePayloadTests
{
    private static readonly PushOptions Options = new() { ChannelId = "print_status", TimeToLiveHours = 24 };
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static FcmMessage Message() => new(
        "device-token",
        "Print finished",
        "Benchy is done",
        new Dictionary<string, string> { ["notificationId"] = "n1", ["printId"] = "42" },
        Now);

    [Fact]
    public void Apns_AsksFirebasexToShowTheNotificationInTheForeground()
    {
        // Without it, firebasex's willPresentNotification never calls the completion
        // handler, so a print finishing while the iOS app is open shows nothing.
        var message = FirebaseFcmClient.BuildMessage(Message(), Options, Now);

        Assert.Equal("true", message.Apns.CustomData["notification_foreground"]);
    }

    [Fact]
    public void Data_IsUnchanged_SoAndroidGetsNoNewKey()
    {
        var message = FirebaseFcmClient.BuildMessage(Message(), Options, Now);

        Assert.Equal(new[] { "notificationId", "printId" }, message.Data.Keys.OrderBy(k => k));
    }

    [Fact]
    public void Apns_KeepsItsExpiryAndCollapseIdentity()
    {
        var message = FirebaseFcmClient.BuildMessage(Message(), Options, Now);

        Assert.Equal(
            Now.AddHours(24).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            message.Apns.Headers["apns-expiration"]);
        Assert.Equal("n1", message.Apns.Aps.ThreadId);
    }
}
