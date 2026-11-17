using Microsoft.Extensions.Options;

namespace PrintLogApi.Services.UserDeletion;

/// <summary>When <see cref="PendingUserDeletionJob"/> runs. The grace period itself is <c>PendingUserDeactivationTimeInMinutes</c>.</summary>
public sealed class PendingUserDeletionOptions
{
    public const string SectionName = "PendingUserDeletion";

    /// <summary>
    /// Off stops the job entirely, and then nothing deletes deactivated accounts. Forced off in
    /// E2ETesting (<see cref="PendingUserDeletionServiceCollectionExtensions"/>).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Time between runs. Two hours, as the Azure Function's <c>0 0 */2 * * *</c> schedule was, so an
    /// account is deleted at most two hours after its grace period ends.
    /// </summary>
    public TimeSpan RunInterval { get; set; } = TimeSpan.FromHours(2);
}

public sealed class PendingUserDeletionOptionsValidator : IValidateOptions<PendingUserDeletionOptions>
{
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaximumInterval = TimeSpan.FromDays(1);

    public ValidateOptionsResult Validate(string? name, PendingUserDeletionOptions options)
    {
        // A zero or negative period makes PeriodicTimer throw; a tiny one runs a heavy,
        // multi-table query back to back; one past the one-day grace period lets deleted accounts
        // linger for longer than users are told.
        if (options.RunInterval < MinimumInterval || options.RunInterval > MaximumInterval)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(PendingUserDeletionOptions.RunInterval)} must be between {MinimumInterval} and {MaximumInterval}.");
        }

        return ValidateOptionsResult.Success;
    }
}
