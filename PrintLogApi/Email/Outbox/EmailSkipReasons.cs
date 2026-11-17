namespace PrintLogApi.Email.Outbox;

/// <summary>Values of <see cref="Models.EmailOutbox.SkipReason"/> (spec §5.5). Stored, so never rename one.</summary>
public static class EmailSkipReasons
{
    public const string Holdout = "holdout";
    public const string Unverified = "unverified";
    public const string Suppressed = "suppressed";
    public const string OptedOut = "opted-out";
    public const string NotRelevant = "not-relevant";
    public const string UserMissing = "user-missing";
    public const string Deactivating = "deactivating";
    public const string Expired = "expired";
    public const string DisabledCampaign = "disabled-campaign";
    public const string DryRun = "dry-run";
}
