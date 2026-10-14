using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using PrintLog.SlicerSettings;
using PrintLogApi.Achievements;
using PrintLogApi.Exceptions;
using PrintLogApi.Models;
using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Services.SlicerSettings;

public interface IPrintSlicerSettingsService
{
    /// <summary>
    /// Records a connector's slicer settings for one of the user's own prints (#164): parses them,
    /// stores the settings record, sets <see cref="Print.Slicer"/> and
    /// <see cref="Print.SlicerVersion"/>, and puts the settings note in the print's notes. Never
    /// changes how the print was logged (<see cref="Print.Source"/>, <see cref="Print.ExternalSource"/>).
    /// A resend replaces what an earlier one recorded.
    /// </summary>
    /// <exception cref="DoesNotExistException">The print is missing or not the user's.</exception>
    /// <exception cref="BadRequestException">Nothing usable was sent.</exception>
    /// <exception cref="SlicerSettingsConflictException">The print's settings came from a slicer plugin.</exception>
    Task<PrintSlicerSettingsResultDto> Record(long printId, PrintSlicerSettingsDto dto, long userId, CancellationToken ct);
}

public sealed class PrintSlicerSettingsService(
    PrintLogContext context,
    ICacheVersionService cacheVersionService,
    TelemetryClient telemetry) : IPrintSlicerSettingsService
{
    private const string ConfigBlockSource = "configBlock";
    private const string MetadataSource = "metadata";

    /// <summary>What the library reports when the header did not name a version.</summary>
    private const string UnknownVersion = "Unknown";

    private sealed record Parsed(
        string Source,
        string Slicer,
        string? Version,
        bool? IsRecognized,
        SortedDictionary<string, string> Settings,
        string Note,
        int? EstimatedPrintTimeSeconds,
        int? TotalFilamentMg);

    public async Task<PrintSlicerSettingsResultDto> Record(long printId, PrintSlicerSettingsDto dto, long userId, CancellationToken ct)
    {
        // Parsed before any read: it needs nothing from the database, and a request that is going
        // to be rejected should not cost a query.
        var parsed = Parse(dto);

        // One retry, for the unique index on CuraSettings.PrintId: two first sends racing for the
        // same print both find no record, and the loser's whole save (notes included) rolls back.
        // On the retry it finds the winner's record and replaces it, as any resend would.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await Save(printId, parsed, userId, ct);
                cacheVersionService.InvalidateUserCache(userId);

                telemetry.TrackEvent("PrintSlicerSettings", new Dictionary<string, string>
                {
                    ["Source"] = parsed.Source,
                    ["Slicer"] = result.Slicer ?? string.Empty,
                    ["Recognized"] = parsed.IsRecognized?.ToString() ?? string.Empty,
                    ["Replaced"] = result.Replaced.ToString(),
                    ["NoteAppended"] = result.NoteAppended.ToString(),
                });

                return result;
            }
            catch (DbUpdateException) when (attempt == 1)
            {
                context.ChangeTracker.Clear();
            }
        }
    }

    private async Task<PrintSlicerSettingsResultDto> Save(long printId, Parsed parsed, long userId, CancellationToken ct)
    {
        // Creator-only, and a foreign id is the same 404 as a missing one: no existence oracle.
        var print = await context.Prints
            .FirstOrDefaultAsync(p => p.Id == printId && p.CreatedById == userId, ct)
            ?? throw new DoesNotExistException();

        var record = await context.CuraSettings.FirstOrDefaultAsync(c => c.PrintId == printId, ct);

        // A slicer with no connector record means the Cura plugin or the Slic3r uploader recorded
        // the settings (ApplyProvenance). They read the whole file on the slicer's machine, with
        // the user's presets and the toolpaths, so they are always the richer record; a connector
        // does not get to replace them. GET /api/Prints/claimable reports this as hasSlicerSettings.
        if (record is null && print.Slicer is not null)
        {
            throw new SlicerSettingsConflictException(
                "This print's slicer settings were recorded by a slicer plugin, and a connector cannot replace them.");
        }

        var replaced = record is not null;
        if (record is null)
        {
            record = new CuraSetting
            {
                PrintId = printId,
                UserId = userId,
                CreatedDate = DateTimeOffset.UtcNow,
            };
            context.CuraSettings.Add(record);
        }

        record.Slicer = Truncate(parsed.Slicer, 50);
        record.CuraVersion = Truncate(parsed.Version, 100);
        record.PluginVersion = null;
        record.Settings = StoredSettings(parsed);

        var slicer = SlicerNames.Normalize(parsed.Slicer);
        var version = Truncate(parsed.Version, 50);
        print.Slicer = slicer;
        print.SlicerVersion = version;

        var notes = SlicerSettingsNote.Upsert(print.Notes, SlicerSettingsNote.Section(parsed.Slicer, version, parsed.Note));
        var noteAppended = notes.Length <= SlicerSettingsNote.MaxNotesLength;
        if (noteAppended)
        {
            print.Notes = notes;
        }

        print.UpdatedById = userId;
        await context.SaveChangesAsync(ct);

        return new PrintSlicerSettingsResultDto
        {
            PrintId = printId,
            Slicer = slicer,
            SlicerVersion = version,
            Source = parsed.Source,
            IsRecognized = parsed.IsRecognized,
            SettingsCount = parsed.Settings.Count,
            Replaced = replaced,
            NoteAppended = noteAppended,
            Note = parsed.Note,
        };
    }

    /// <summary>
    /// The settings block when it holds settings, otherwise the metadata. Credential and host
    /// values are scrubbed from the text before the parser sees it.
    /// </summary>
    private static Parsed Parse(PrintSlicerSettingsDto dto)
    {
        var metadata = dto.Metadata;
        var metadataSlicer = SlicerSettingsNote.OneLine(metadata?.Slicer);
        var metadataVersion = SlicerSettingsNote.OneLine(metadata?.SlicerVersion);

        if (dto.ConfigBlock is { } block && !string.IsNullOrWhiteSpace(block))
        {
            var header = dto.Header is { } h ? SlicerSettingsScrubber.ScrubText(h) : null;
            // No debug log: its messages can quote the text, and nothing here should echo it.
            var result = SlicerConfigParser.Parse(SlicerSettingsScrubber.ScrubText(block), header);
            var settings = SlicerSettingsScrubber.ScrubSettings(result.Settings);

            if (settings.Count > 0)
            {
                // Without a header the slicer is the parser's best guess from the settings, and
                // Orca forks all read as Orca; a slicer the connector names is better than a guess.
                var slicer = !result.IsRecognized && metadataSlicer.Length > 0 ? metadataSlicer : result.Slicer;
                var version = result.Version != UnknownVersion ? result.Version
                    : metadataVersion.Length > 0 ? metadataVersion
                    : null;
                return new Parsed(
                    ConfigBlockSource, slicer, version, result.IsRecognized, settings, result.Note,
                    result.EstimatedPrintTimeSeconds, result.TotalFilamentMg);
            }

            if (metadata is null)
            {
                throw new BadRequestException("configBlock holds no slicer settings. Send the `; key = value` lines the slicer wrote, or metadata.");
            }
        }

        if (metadata is null)
        {
            // PrintSlicerSettingsDto.Validate has already rejected a body with neither.
            throw new BadRequestException("Send configBlock, metadata, or both.");
        }

        if (metadataSlicer.Length == 0)
        {
            // SlicerNames reads a missing name as the Cura plugin, so it cannot default.
            throw new BadRequestException("metadata.slicer is required when there is no configBlock with settings.");
        }

        return new Parsed(
            MetadataSource,
            metadataSlicer,
            metadataVersion.Length > 0 ? metadataVersion : null,
            IsRecognized: null,
            MetadataSettings(metadata),
            SlicerSettingsNote.Render(metadata),
            EstimatedPrintTimeSeconds: null,
            TotalFilamentMg: null);
    }

    /// <summary>The metadata under the setting names the slicers themselves use.</summary>
    private static SortedDictionary<string, string> MetadataSettings(SlicerMetadataDto metadata)
    {
        var settings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                settings[key] = value;
            }
        }

        Add("layer_height", SlicerSettingsNote.Number(metadata.LayerHeightMm));
        Add("first_layer_height", SlicerSettingsNote.Number(metadata.FirstLayerHeightMm));
        Add("nozzle_diameter", SlicerSettingsNote.Number(metadata.NozzleDiameterMm));
        Add("filament_settings_id", SlicerSettingsNote.OneLine(metadata.FilamentName));
        Add("filament_type", SlicerSettingsNote.OneLine(metadata.FilamentType));
        Add("first_layer_temperature", SlicerSettingsNote.Number(metadata.FirstLayerNozzleTemperatureC));
        Add("first_layer_bed_temperature", SlicerSettingsNote.Number(metadata.FirstLayerBedTemperatureC));
        return settings;
    }

    /// <summary>
    /// The record's <c>Settings</c> JSON. <c>note</c>, <c>estimated_print_time_seconds</c> and
    /// <c>material_used_mg</c> are the names the Slic3r uploader posts; <c>settings</c> is every
    /// parsed setting, scrubbed, and <c>source</c> says what they came from.
    /// </summary>
    private static JsonElement StoredSettings(Parsed parsed)
    {
        var settings = new JsonObject();
        foreach (var (key, value) in parsed.Settings)
        {
            settings[key] = value;
        }

        var stored = new JsonObject
        {
            ["source"] = parsed.Source,
            ["note"] = parsed.Note,
            ["estimated_print_time_seconds"] = parsed.EstimatedPrintTimeSeconds,
            ["material_used_mg"] = parsed.TotalFilamentMg,
            ["settings"] = settings,
        };
        return JsonSerializer.SerializeToElement(stored);
    }

    private static string? Truncate(string? value, int length) =>
        value is { } v && v.Length > length ? v[..length] : value;
}
