using Prometheus;

namespace PrintLogApi.Email;

/// <summary>Prometheus series for the email pipeline (spec §6.11), scraped with the existing /metrics.</summary>
public static class EmailMetrics
{
    public static readonly Counter Sent = Metrics.CreateCounter(
        "printlog_email_sent_total", "Emails accepted by the provider.", "campaign");

    public static readonly Counter Skipped = Metrics.CreateCounter(
        "printlog_email_skipped_total", "Outbox rows skipped, by reason.", "campaign", "reason");

    public static readonly Counter Failed = Metrics.CreateCounter(
        "printlog_email_failed_total", "Outbox rows that failed, by failure kind.", "campaign", "kind");

    public static readonly Counter Bounced = Metrics.CreateCounter(
        "printlog_email_bounced_total", "Permanent bounces reported by the provider.");

    public static readonly Counter Complained = Metrics.CreateCounter(
        "printlog_email_complained_total", "Spam complaints reported by the provider.");

    public static readonly Gauge OutboxPending = Metrics.CreateGauge(
        "printlog_email_outbox_pending", "Outbox rows waiting to be sent.");
}
