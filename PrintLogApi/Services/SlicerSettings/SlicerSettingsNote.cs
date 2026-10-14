using System.Globalization;
using System.Text;
using PrintLogApi.Models.DTOs.Print;

namespace PrintLogApi.Services.SlicerSettings;

/// <summary>
/// The settings section a connector's slicer settings add to a print's notes (#164).
/// <para>
/// The section sits between a heading line and an end line, so a resend can find it and replace
/// it rather than add a second one, and everything outside it, which is the user's, stays as it
/// is. Notes are plain text in the web app, so the markers are plain text too. If the user deletes
/// either marker the section can no longer be found, and a resend appends a fresh one: a duplicate
/// is recoverable, deleting text the user wrote is not.
/// </para>
/// </summary>
public static class SlicerSettingsNote
{
    /// <summary>The start of the heading line. The slicer and its version follow it.</summary>
    public const string HeadingPrefix = "--- Slicer settings";

    /// <summary>The line that closes the section.</summary>
    public const string EndLine = "--- End of slicer settings ---";

    /// <summary>
    /// The most notes the print forms accept (<c>[StringLength(50000)]</c> on the PUT and PATCH
    /// DTOs). Notes past it would make the print impossible to save from the web app.
    /// </summary>
    public const int MaxNotesLength = 50_000;

    /// <summary>The whole section: heading, the note, end line. No trailing newline.</summary>
    public static string Section(string slicer, string? version, string note)
    {
        var label = string.IsNullOrWhiteSpace(version) ? slicer : $"{slicer} {version}";
        return $"{HeadingPrefix} ({OneLine(label)}) ---\n{note}\n{EndLine}";
    }

    /// <summary>
    /// The notes with <paramref name="section"/> in place of the existing section, or after the
    /// user's notes, separated by a blank line, when there is none.
    /// </summary>
    public static string Upsert(string? notes, string section)
    {
        if (string.IsNullOrEmpty(notes))
        {
            return section;
        }

        if (TryFind(notes, out var start, out var end))
        {
            return string.Concat(notes.AsSpan(0, start), section, notes.AsSpan(end));
        }

        var separator = notes.EndsWith("\n\n", StringComparison.Ordinal) || notes.EndsWith("\r\n\r\n", StringComparison.Ordinal) ? ""
            : notes.EndsWith('\n') ? "\n"
            : "\n\n";
        return string.Concat(notes, separator, section);
    }

    /// <summary>
    /// The note for a metadata summary, one line per field that has a value. Field names follow the
    /// slicers' own labels where the default notes use them.
    /// </summary>
    public static string Render(SlicerMetadataDto metadata)
    {
        var lines = new StringBuilder();
        void Line(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Append(label).Append(": ").Append(value).Append('\n');
            }
        }

        Line("Layer Height", Millimetres(metadata.LayerHeightMm));
        Line("First Layer Height", Millimetres(metadata.FirstLayerHeightMm));
        Line("Nozzle Diameter", Millimetres(metadata.NozzleDiameterMm));
        var name = OneLine(metadata.FilamentName);
        var type = OneLine(metadata.FilamentType);
        Line("Filament", name.Length > 0 && type.Length > 0 ? $"{name} ({type})" : name.Length > 0 ? name : type);
        Line("First Layer Nozzle Temperature", Celsius(metadata.FirstLayerNozzleTemperatureC));
        Line("First Layer Bed Temperature", Celsius(metadata.FirstLayerBedTemperatureC));

        return lines.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Free text from a connector on one line. A newline in a filament name could otherwise forge
    /// the end line and move where a resend thinks the section stops.
    /// </summary>
    public static string OneLine(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    public static string? Number(double? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Millimetres(double? value) => value is null ? null : $"{Number(value)} mm";

    private static string? Celsius(double? value) => value is null ? null : $"{Number(value)} °C";

    /// <summary>
    /// The first heading line and the first end line after it, as [start, end): from the start of
    /// the heading line to the end of the end line, excluding its line break.
    /// </summary>
    private static bool TryFind(string notes, out int start, out int end)
    {
        start = -1;
        end = -1;
        var lineStart = 0;
        while (lineStart <= notes.Length)
        {
            var newline = notes.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? notes.Length : newline;
            var line = notes.AsSpan(lineStart, lineEnd - lineStart).TrimEnd('\r');

            if (start < 0)
            {
                if (line.StartsWith(HeadingPrefix, StringComparison.Ordinal) && line.EndsWith("---", StringComparison.Ordinal))
                {
                    start = lineStart;
                }
            }
            else if (line.SequenceEqual(EndLine))
            {
                // Before a "\r" too, so a CRLF line break after the section survives.
                end = lineStart + line.Length;
                return true;
            }

            if (newline < 0)
            {
                break;
            }

            lineStart = newline + 1;
        }

        return false;
    }
}
