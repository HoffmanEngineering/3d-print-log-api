using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrintLogApi.Email;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Seo;
using PrintLogApi.Models.DTOs.Sitemap;

namespace PrintLogApi.Services;

/// <summary>
/// Builds the <c>/seo</c> metadata for public prints, projects and profiles. Every method returns
/// null unless the entity is Public, the line the sitemap and the <c>/public</c> id lists draw:
/// an Unlisted page is reachable by link but must not be indexed, and a private one must not be
/// described at all. Only fields the public page shows are read.
/// </summary>
/// <remarks>
/// Not cached in process. The lookups are single-row reads by key, and the keys are anonymous
/// and enumerable, so a HybridCache entry per id would let any crawler churn the 8 MiB budget
/// the per-user summaries depend on. The responses are edge-cacheable instead (see the controllers).
/// </remarks>
public sealed partial class SeoMetadataService(
    PrintLogContext context,
    SocialImageLookup socialImages,
    IOptions<EmailOptions> emailOptions)
{
    public const string SiteName = "3D Print Log";
    public const int MaxDescriptionLength = 155;

    private const string TitleSuffix = " | " + SiteName;
    private const string UnnamedUser = "a 3D Print Log user";

    private string Web => emailOptions.Value.WebBaseUrl.TrimEnd('/');

    /// <param name="apiBaseUrl">Absolute base URL of this API, for the social-image URL.</param>
    public async Task<SeoMetadataDto?> ForPrintAsync(long printId, string apiBaseUrl, CancellationToken ct = default)
    {
        var print = await context.Prints
            .AsNoTracking()
            .Where(p => p.Id == printId && p.ViewStatus == Print.PrintViewStatus.Public)
            .Select(p => new
            {
                p.Title,
                p.Notes,
                p.StartDate,
                p.Status,
                p.PrintTimeInSeconds,
                p.EstimatedPrintTimeInSeconds,
                p.FilamentType,
                p.CreatedDate,
                p.UpdatedDate,
                PrinterMake = p.Printer.Make,
                PrinterModel = p.Printer.Model,
                AuthorId = p.CreatedById,
                AuthorName = p.CreatedBy.DisplayName,
                AuthorViewStatus = p.CreatedBy.ViewStatus,
                Materials = p.FilamentUsage!
                    .Where(u => u.Filament != null)
                    .OrderBy(u => u.Slot)
                    .Select(u => new MaterialRow(
                        u.Filament!.DisplayName, u.Filament.Brand, u.Filament.MaterialType, u.Filament.ColorName))
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (print is null)
            return null;

        var title = string.IsNullOrWhiteSpace(print.Title) ? "Untitled print" : print.Title.Trim();
        var author = Author(print.AuthorId, print.AuthorName, print.AuthorViewStatus);

        var description = PlainText(print.Notes);
        if (description.Length == 0)
        {
            var by = author?.Name ?? UnnamedUser;
            description = print.StartDate is { } started
                ? $"{title} printed by {by} on {started.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture)}"
                : $"{title} printed by {by}";
        }

        var facts = new List<SeoFactDto>();
        AddFact(facts, "Printer", $"{print.PrinterMake} {print.PrinterModel}");
        var materials = print.Materials.Select(MaterialLabel).OfType<string>().Distinct().ToList();
        if (materials.Count > 0)
            AddFact(facts, materials.Count == 1 ? "Material" : "Materials", string.Join(", ", materials));
        else
            AddFact(facts, "Material", print.FilamentType);
        if (print.PrintTimeInSeconds is > 0)
            AddFact(facts, "Print time", Duration(print.PrintTimeInSeconds.Value));
        else if (print.EstimatedPrintTimeInSeconds is > 0)
            AddFact(facts, "Estimated time", Duration(print.EstimatedPrintTimeInSeconds.Value));
        AddFact(facts, "Status", StatusLabel(print.Status));

        var image = await socialImages.ForPrintAsync(printId, ct);

        return new SeoMetadataDto
        {
            Type = "print",
            Title = title + TitleSuffix,
            Description = Truncate(description),
            CanonicalUrl = $"{Web}/prints/{printId}",
            Image = image is null ? null : new SeoImageDto
            {
                Url = $"{apiBaseUrl}{ImageUrls.PrintSocialImage(printId)}?v={image.Version}",
                Width = image.Width,
                Height = image.Height,
                Alt = title,
            },
            Author = author,
            PublishedAt = Utc(print.CreatedDate),
            UpdatedAt = Utc(print.UpdatedDate),
            Facts = facts,
        };
    }

    /// <inheritdoc cref="ForPrintAsync"/>
    public async Task<SeoMetadataDto?> ForProjectAsync(Guid projectId, string apiBaseUrl, CancellationToken ct = default)
    {
        var project = await context.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId && p.ViewStatus == Project.PubliclyListedViewStatus)
            .Select(p => new
            {
                p.Name,
                p.Description,
                p.Status,
                p.CreatedDate,
                p.UpdatedDate,
                // Every print in the project, as the project page's own count does.
                PrintCount = p.Prints!.Count(),
                AuthorId = p.CreatedById,
                AuthorName = p.CreatedBy.DisplayName,
                AuthorViewStatus = p.CreatedBy.ViewStatus,
            })
            .FirstOrDefaultAsync(ct);
        if (project is null)
            return null;

        var name = string.IsNullOrWhiteSpace(project.Name) ? "Untitled project" : project.Name.Trim();
        var author = Author(project.AuthorId, project.AuthorName, project.AuthorViewStatus);
        var prints = project.PrintCount == 1 ? "1 print" : $"{project.PrintCount} prints";

        var description = PlainText(project.Description);
        if (description.Length == 0)
        {
            description = author is null
                ? $"{name}, a 3D printing project on {SiteName} with {prints}"
                : $"{name}, a 3D printing project by {author.Name} with {prints}";
        }

        var facts = new List<SeoFactDto>();
        AddFact(facts, "Prints", project.PrintCount.ToString(CultureInfo.InvariantCulture));
        AddFact(facts, "Status", project.Status switch
        {
            Project.ProjectStatus.InProgress => "In progress",
            Project.ProjectStatus.Complete => "Complete",
            Project.ProjectStatus.OnHold => "On hold",
            Project.ProjectStatus.Cancelled => "Canceled",
            _ => null,
        });

        var image = await socialImages.ForProjectAsync(projectId, ct);

        return new SeoMetadataDto
        {
            Type = "project",
            Title = name + TitleSuffix,
            Description = Truncate(description),
            CanonicalUrl = $"{Web}/projects/{projectId}",
            Image = image is null ? null : new SeoImageDto
            {
                Url = $"{apiBaseUrl}{ImageUrls.ProjectSocialImage(projectId)}?v={image.Version}",
                Width = image.Width,
                Height = image.Height,
                Alt = name,
            },
            Author = author,
            PublishedAt = Utc(project.CreatedDate),
            UpdatedAt = Utc(project.UpdatedDate),
            Facts = facts,
        };
    }

    /// <summary>A Public profile's metadata, or null. Takes no API base: its image is the profile picture.</summary>
    public async Task<SeoMetadataDto?> ForUserAsync(long userId, CancellationToken ct = default)
    {
        var user = await context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.ViewStatus == User.ProfileViewStatus.Public)
            .Select(u => new
            {
                u.DisplayName,
                u.Bio,
                u.ProfilePicture,
                u.CreatedDate,
                PublicPrints = context.Prints.Count(p => p.CreatedById == u.Id && p.ViewStatus == Print.PrintViewStatus.Public),
            })
            .FirstOrDefaultAsync(ct);
        if (user is null)
            return null;

        var name = string.IsNullOrWhiteSpace(user.DisplayName) ? "3D Print Log user" : user.DisplayName.Trim();
        var canonical = $"{Web}/users/{userId}";

        var description = PlainText(user.Bio);
        if (description.Length == 0)
            description = $"View {name}'s 3D prints and projects on 3DPrintLog.com";

        var picture = Uri.TryCreate(user.ProfilePicture, UriKind.Absolute, out var pictureUri)
            && (pictureUri.Scheme == Uri.UriSchemeHttps || pictureUri.Scheme == Uri.UriSchemeHttp)
                ? pictureUri.AbsoluteUri
                : null;

        return new SeoMetadataDto
        {
            Type = "profile",
            Title = $"{name} Profile{TitleSuffix}",
            Description = Truncate(description),
            CanonicalUrl = canonical,
            Image = picture is null ? null : new SeoImageDto { Url = picture, Alt = $"{name}'s profile picture" },
            Author = new SeoAuthorDto { Name = name, Url = canonical },
            PublishedAt = user.CreatedDate is { } created && created.UtcDateTime >= SitemapEntryDto.EarliestPlausible
                ? created.ToUniversalTime()
                : null,
            UpdatedAt = null,
            Facts = [new SeoFactDto { Label = "Public prints", Value = user.PublicPrints.ToString(CultureInfo.InvariantCulture) }],
        };
    }

    private sealed record MaterialRow(string? DisplayName, string? Brand, string? MaterialType, string? ColorName);

    /// <summary>
    /// The byline. Named only when the creator set a display name, as the print page's byline is,
    /// and linked only when their profile is Public.
    /// </summary>
    private SeoAuthorDto? Author(long userId, string? displayName, User.ProfileViewStatus viewStatus)
        => string.IsNullOrWhiteSpace(displayName)
            ? null
            : new SeoAuthorDto
            {
                Name = displayName.Trim(),
                Url = viewStatus == User.ProfileViewStatus.Public ? $"{Web}/users/{userId}" : null,
            };

    /// <summary>What the materials list shows: the material's name, then its color in parentheses.</summary>
    private static string? MaterialLabel(MaterialRow row)
    {
        var name = !string.IsNullOrWhiteSpace(row.DisplayName)
            ? row.DisplayName.Trim()
            : string.Join(' ', new[] { row.Brand, row.MaterialType }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        if (name.Length == 0)
            return null;
        return string.IsNullOrWhiteSpace(row.ColorName) ? name : $"{name} ({row.ColorName.Trim()})";
    }

    private static void AddFact(List<SeoFactDto> facts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            facts.Add(new SeoFactDto { Label = label, Value = value.Trim() });
    }

    private static string StatusLabel(Print.PrintStatus status) => status switch
    {
        Print.PrintStatus.Pending => "Pending",
        Print.PrintStatus.Printing => "Printing",
        Print.PrintStatus.Success => "Success",
        Print.PrintStatus.Cancelled => "Canceled",
        Print.PrintStatus.Failed => "Failed",
        Print.PrintStatus.PartialSuccess => "Partial success",
        _ => status.ToString(),
    };

    /// <summary><c>1h 30m</c>, <c>2h</c>, <c>45m</c>, <c>30s</c>.</summary>
    internal static string Duration(int seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        var hours = (int)span.TotalHours;
        if (hours > 0)
            return span.Minutes > 0 ? $"{hours}h {span.Minutes}m" : $"{hours}h";
        return span.Minutes > 0 ? $"{span.Minutes}m" : $"{span.Seconds}s";
    }

    /// <summary>Whitespace runs (line breaks included) collapsed to one space, trimmed.</summary>
    internal static string PlainText(string? text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : Whitespace().Replace(text, " ").Trim();

    /// <summary>
    /// At most <see cref="MaxDescriptionLength"/> characters, the length search results show:
    /// cut at the last word boundary that fits, with an ellipsis.
    /// </summary>
    internal static string Truncate(string text)
    {
        if (text.Length <= MaxDescriptionLength)
            return text;

        var cut = text[..(MaxDescriptionLength - 1)];
        var lastSpace = cut.LastIndexOf(' ');
        // A single very long word is cut mid-word rather than reduced to almost nothing.
        if (lastSpace > MaxDescriptionLength / 2)
            cut = cut[..lastSpace];
        return cut.TrimEnd(' ', ',', ';', ':', '.', '-') + "…";
    }

    /// <summary>
    /// A <see cref="TimestampEntity"/> column as UTC (it reads back with no kind), or null when it
    /// was never really set. Same rule as the sitemap's <c>lastmod</c>.
    /// </summary>
    private static DateTimeOffset? Utc(DateTime value)
        => value < SitemapEntryDto.EarliestPlausible
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
