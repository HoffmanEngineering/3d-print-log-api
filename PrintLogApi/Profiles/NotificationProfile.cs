using System.Text.Json;
using AutoMapper;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Notification;

namespace PrintLogApi.Profiles;

public class NotificationProfile : Profile
{
    public NotificationProfile()
    {
        // SpecifyKind matches PrintProfile: CreatedDate is stored as UTC but read back from
        // SQL Server as Unspecified, so the kind is reasserted rather than converted.
        CreateMap<Notification, NotificationSummaryDto>()
            .ForMember(dest => dest.PrintTitle, opt => opt.MapFrom(src => src.Print != null ? src.Print.Title : null))
            .ForMember(dest => dest.CreatedDate, opt => opt.MapFrom(src => (DateTimeOffset)DateTime.SpecifyKind(src.CreatedDate, DateTimeKind.Utc)))
            .ForMember(dest => dest.TriggeredByUser, opt => opt.MapFrom(src => src.TriggeredByUser))
            // Parsed in memory: EF evaluates a method call in the top-level projection on the
            // client, after reading the two columns it needs.
            .ForMember(dest => dest.Achievement, opt => opt.MapFrom(src => TryParseAchievement(src.Type, src.Metadata)));

        CreateMap<Notification, NotificationDetailDto>()
            .ForMember(dest => dest.PrintTitle, opt => opt.MapFrom(src => src.Print != null ? src.Print.Title : null))
            .ForMember(dest => dest.CreatedDate, opt => opt.MapFrom(src => (DateTimeOffset)DateTime.SpecifyKind(src.CreatedDate, DateTimeKind.Utc)))
            .ForMember(dest => dest.ReadDate, opt => opt.MapFrom(src => src.ReadDate.HasValue
                ? (DateTimeOffset?)DateTime.SpecifyKind(src.ReadDate.Value, DateTimeKind.Utc)
                : null))
            .ForMember(dest => dest.TriggeredByUser, opt => opt.MapFrom(src => src.TriggeredByUser));
    }

    /// <summary>
    /// Reads version-1 achievement metadata: <c>{"v":1,"key":…,"tier":…}</c> for a grant or
    /// <c>{"v":1,"summary":true,"count":…}</c> for the launch summary. Anything else, including
    /// malformed JSON, a newer version, or a non-achievement notification, yields null rather
    /// than failing the whole notification list.
    /// </summary>
    public static AchievementNotificationDto? TryParseAchievement(NotificationType type, string? metadata)
    {
        if (type != NotificationType.Achievement || string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(metadata);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1)
            {
                return null;
            }

            if (root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.True)
            {
                return new AchievementNotificationDto
                {
                    Summary = true,
                    Count = root.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : null,
                };
            }

            if (root.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
                && root.TryGetProperty("tier", out var tier) && tier.ValueKind == JsonValueKind.Number)
            {
                return new AchievementNotificationDto { Key = key.GetString(), Tier = tier.GetInt32() };
            }

            return null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
