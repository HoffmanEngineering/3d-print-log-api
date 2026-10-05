using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PrintLogApi.Achievements.Metrics;
using PrintLogApi.Models;

namespace PrintLogApi.Email.Campaigns;

/// <summary>Print sources that arrive without the user typing anything: the integrations.</summary>
public static class AutomatedPrintSources
{
    public static readonly PrintSource[] All = [PrintSource.SlicerPlugin, PrintSource.OctoPrint, PrintSource.Moonraker, PrintSource.ApiKey];
}

/// <summary>Reads users' <c>General_TimeZone</c> settings in one query.</summary>
public static class EmailUserZones
{
    public static async Task<Dictionary<long, string?>> LoadAsync(PrintLogContext db, IReadOnlyCollection<long> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        var ids = userIds.Distinct().ToList();
        return await db.UserSettings
            .AsNoTracking()
            .Where(s => s.UserSettingTypeId == EvaluationContext.TimeZoneSettingTypeId && s.UserId != null && ids.Contains(s.UserId.Value))
            .ToDictionaryAsync(s => s.UserId!.Value, s => s.Value, ct);
    }

    public static async Task<string?> LoadAsync(PrintLogContext db, long userId, CancellationToken ct)
        => (await LoadAsync(db, [userId], ct)).GetValueOrDefault(userId);
}

/// <summary>Number formats shared by every email, in invariant culture so output never depends on the server.</summary>
public static class EmailFormat
{
    public static string Count(int n, string singular, string plural) => n == 1 ? $"1 {singular}" : $"{n.ToString("N0", CultureInfo.InvariantCulture)} {plural}";

    /// <summary>Whole hours, at least "1h" for any non-zero time, "0h" for none.</summary>
    public static string Hours(double hours)
        => hours <= 0 ? "0h" : $"{Math.Max(1, Math.Round(hours, MidpointRounding.AwayFromZero)).ToString("N0", CultureInfo.InvariantCulture)}h";

    /// <summary>Grams below a kilogram, kilograms with one decimal above.</summary>
    public static string Weight(double grams)
        => grams >= 1000
            ? $"{(grams / 1000).ToString("0.0", CultureInfo.InvariantCulture)} kg"
            : $"{Math.Round(grams, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.InvariantCulture)} g";

    /// <summary>"Hi Ada," or "Hi there," when there is no usable display name.</summary>
    public static string Greeting(string? displayName)
    {
        var first = displayName?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? "Hi there," : $"Hi {first},";
    }
}
