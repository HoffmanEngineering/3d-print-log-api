using PrintLogApi.Models;

namespace PrintLogApi.Webhooks;

/// <summary>Event names on the wire (#235), and their subscription flags.</summary>
public static class WebhookEventTypes
{
    public const string PrintStarted = "print.started";
    public const string PrintFinished = "print.finished";
    public const string PrintFailed = "print.failed";

    /// <summary>Sent only by the test endpoint; not something a subscription selects.</summary>
    public const string Test = "webhook.test";

    private static readonly (string Name, WebhookEvents Flag)[] Subscribable =
    [
        (PrintStarted, WebhookEvents.PrintStarted),
        (PrintFinished, WebhookEvents.PrintFinished),
        (PrintFailed, WebhookEvents.PrintFailed),
    ];

    /// <summary>
    /// Parses a subscription's event list. Null or empty means every event. False when any name is
    /// unknown, so a typo is a 400 rather than an event that silently never arrives.
    /// </summary>
    public static bool TryParse(IEnumerable<string>? names, out WebhookEvents events)
    {
        events = WebhookEvents.None;
        var list = names?.ToList();
        if (list is null || list.Count == 0)
        {
            events = WebhookEvents.All;
            return true;
        }

        foreach (var name in list)
        {
            var match = Subscribable.FirstOrDefault(s => string.Equals(s.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match.Name is null)
            {
                events = WebhookEvents.None;
                return false;
            }
            events |= match.Flag;
        }
        return true;
    }

    public static List<string> ToNames(WebhookEvents events) =>
        Subscribable.Where(s => events.HasFlag(s.Flag)).Select(s => s.Name).ToList();

    /// <summary>The flag for a subscribable event name; None for anything else (the test event).</summary>
    public static WebhookEvents ToFlag(string name) =>
        Subscribable.FirstOrDefault(s => s.Name == name).Flag;
}
