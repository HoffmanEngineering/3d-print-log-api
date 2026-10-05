using Microsoft.Extensions.Options;
using PrintLogApi.Services;

namespace PrintLogApi.Email;

/// <summary>The <c>Email</c> configuration section. Defaults are safe: nothing is sent until <see cref="Enabled"/> is true.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Master kill switch. While false, the dispatcher sends nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>Render and record telemetry without sending; rows end <c>Skipped(dry-run)</c>.</summary>
    public bool DryRun { get; set; }

    /// <summary>When non-empty, only these users receive email (others wait until it is lifted).</summary>
    public long[] AllowListUserIds { get; set; } = [];

    /// <summary>Maximum sends per UTC day; null means unlimited.</summary>
    public int? DailyCap { get; set; }

    public int MaxSendsPerSecond { get; set; } = 5;

    public int BatchSize { get; set; } = 50;

    public TimeSpan EvaluatorInterval { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan DispatchInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan FrequencyCapWindow { get; set; } = TimeSpan.FromDays(3);

    public int HoldoutPercent { get; set; } = 10;

    public string DefaultTimeZone { get; set; } = "America/Chicago";

    public int LocalSendHour { get; set; } = 9;

    public string FromAddress { get; set; } = "updates@mail.3dprintlog.com";

    public string FromName { get; set; } = "3D Print Log";

    public string ReplyTo { get; set; } = "hello@3dprintlog.com";

    /// <summary>Required by CAN-SPAM in every commercial email. A PO box or virtual mailbox, never a home address.</summary>
    public string PostalAddress { get; set; } = "";

    public string WebBaseUrl { get; set; } = "";

    public string ApiBaseUrl { get; set; } = "";

    /// <summary>Base64 keys of at least 32 bytes. The first signs; all verify, so keys can rotate.</summary>
    public string[] UnsubscribeSigningKeys { get; set; } = [];

    /// <summary>Base64 peppers for suppression hashes. The first is current; all are checked on lookup. Never rotate in place.</summary>
    public string[] SuppressionPeppers { get; set; } = [];

    public TimeSpan ManageTokenLifetime { get; set; } = TimeSpan.FromDays(60);

    public NoticeOptions Notice { get; set; } = new();

    public SesOptions Ses { get; set; } = new();

    public OnboardingOptions Onboarding { get; set; } = new();

    public RateLimitOptions RateLimits { get; set; } = new();

    /// <summary>Keyed by <see cref="CampaignKey"/>, not by the campaign name itself.</summary>
    public Dictionary<string, CampaignOptions> Campaigns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsCampaignEnabled(string name) => Campaigns.TryGetValue(CampaignKey(name), out var campaign) && campaign.Enabled;

    /// <summary>
    /// The configuration key for a campaign: its name with underscores for hyphens, so
    /// <c>monthly-recap</c> is switched by <c>Email__Campaigns__monthly_recap__Enabled</c>.
    /// App Service on Linux rejects an app setting whose name contains a hyphen.
    /// </summary>
    public static string CampaignKey(string name) => name.Replace('-', '_');

    /// <summary>True when the pipeline does real work: sending, or rendering in dry run.</summary>
    public bool IsActive => Enabled || DryRun;
}

public sealed class NoticeOptions
{
    /// <summary>When true, only users who have seen the in-app email notice receive engagement email.</summary>
    public bool Required { get; set; } = true;
}

public sealed class SesOptions
{
    public string Region { get; set; } = "us-east-2";

    public string AccessKeyId { get; set; } = "";

    public string SecretAccessKey { get; set; } = "";

    public string ConfigurationSet { get; set; } = "";

    public string EventsTopicArn { get; set; } = "";
}

public sealed class OnboardingOptions
{
    /// <summary>Users created on or after this instant get the onboarding series; null disables it.</summary>
    public DateTimeOffset? StartDate { get; set; }
}

public sealed class RateLimitOptions
{
    /// <summary>Per-token requests per minute on the token endpoints; 0 or less disables.</summary>
    public int PerTokenPerMinute { get; set; } = 10;

    /// <summary>All requests per minute across the token endpoints; 0 or less disables.</summary>
    public int GlobalPerMinute { get; set; } = 300;

    /// <summary>SES event webhook requests per minute; 0 or less disables.</summary>
    public int EventsPerMinute { get; set; } = 600;
}

public sealed class CampaignOptions
{
    public bool Enabled { get; set; }
}

public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    private const int MinimumKeyBytes = 32;

    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        var failures = new List<string>();

        if (options.MaxSendsPerSecond <= 0) failures.Add($"{nameof(EmailOptions.MaxSendsPerSecond)} must be positive.");
        if (options.BatchSize <= 0) failures.Add($"{nameof(EmailOptions.BatchSize)} must be positive.");
        if (options.EvaluatorInterval <= TimeSpan.Zero) failures.Add($"{nameof(EmailOptions.EvaluatorInterval)} must be positive.");
        if (options.DispatchInterval <= TimeSpan.Zero) failures.Add($"{nameof(EmailOptions.DispatchInterval)} must be positive.");
        if (options.FrequencyCapWindow <= TimeSpan.Zero) failures.Add($"{nameof(EmailOptions.FrequencyCapWindow)} must be positive.");
        if (options.LocalSendHour is < 0 or > 23) failures.Add($"{nameof(EmailOptions.LocalSendHour)} must be 0-23.");
        if (options.HoldoutPercent is < 0 or > 100) failures.Add($"{nameof(EmailOptions.HoldoutPercent)} must be 0-100.");
        if (options.DailyCap is < 0) failures.Add($"{nameof(EmailOptions.DailyCap)} must be null or non-negative.");

        foreach (var key in options.Campaigns.Keys.Where(k => k.Contains('-')))
        {
            failures.Add($"Campaigns:{key} is never read. Use Campaigns:{EmailOptions.CampaignKey(key)}: hyphens can't be set as App Service settings.");
        }

        if (options.IsActive)
        {
            RequireText(failures, options.PostalAddress, nameof(EmailOptions.PostalAddress));
            RequireText(failures, options.FromAddress, nameof(EmailOptions.FromAddress));
            RequireText(failures, options.WebBaseUrl, nameof(EmailOptions.WebBaseUrl));
            RequireText(failures, options.ApiBaseUrl, nameof(EmailOptions.ApiBaseUrl));
            RequireKeys(failures, options.UnsubscribeSigningKeys, nameof(EmailOptions.UnsubscribeSigningKeys));
            RequireKeys(failures, options.SuppressionPeppers, nameof(EmailOptions.SuppressionPeppers));

            if (!TimeZoneResolver.TryResolve(options.DefaultTimeZone, out _))
            {
                failures.Add($"{nameof(EmailOptions.DefaultTimeZone)} '{options.DefaultTimeZone}' is not a recognized time zone.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequireText(List<string> failures, string? value, string property)
    {
        if (string.IsNullOrWhiteSpace(value)) failures.Add($"{property} is required when email is enabled or in dry run.");
    }

    private static void RequireKeys(List<string> failures, string[]? keys, string property)
    {
        if (keys is null || keys.Length == 0)
        {
            failures.Add($"{property} needs at least one key when email is enabled or in dry run.");
            return;
        }

        foreach (var key in keys)
        {
            if (!TryDecode(key, out var bytes) || bytes.Length < MinimumKeyBytes)
            {
                failures.Add($"{property} entries must be base64 and at least {MinimumKeyBytes} bytes.");
                return;
            }
        }
    }

    internal static bool TryDecode(string? base64, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(base64)) return false;
        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
