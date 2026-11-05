using System.Globalization;
using System.Text.RegularExpressions;

namespace PrintLogApi.Services.ImageBackfill;

/// <summary>One row the backfill converted, and where its log line says the original was archived.</summary>
public sealed record ArchivedOriginal(ImageRef Image, string Container, string BlobName);

/// <summary>What <see cref="BackfillLog.Parse"/> read.</summary>
/// <param name="Entries">One per "original archived as" line, in log order.</param>
/// <param name="Rejected">Lines that looked like one but name an archive of the other kind.</param>
public sealed record BackfillLogEntries(IReadOnlyList<ArchivedOriginal> Entries, IReadOnlyList<string> Rejected);

/// <summary>
/// Reads the output of an applied <c>image-backfill</c> run for the line it writes per converted
/// row: <c>print-image 123: image/png -> image/jpeg + ..., original archived as printimages-archive/&lt;name&gt;</c>.
/// Everything else in the file (the header, dry-run and derivative-only lines, the summary) is ignored.
/// </summary>
public static partial class BackfillLog
{
    [GeneratedRegex(
        @"\b(?<kind>print-image|project-image) (?<id>\d+): .*, original archived as (?<container>[a-z0-9-]+)/(?<name>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ArchivedLine();

    public static BackfillLogEntries Parse(IEnumerable<string> lines)
    {
        var entries = new List<ArchivedOriginal>();
        var rejected = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r', '\n', ' ', '\t');
            var match = ArchivedLine().Match(line);
            if (!match.Success)
                continue;
            if (!int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                rejected.Add(line);
                continue;
            }

            var kind = match.Groups["kind"].Value;
            var container = match.Groups["container"].Value;
            var expected = kind == ImageRef.PrintImage ? BlobContainers.PrintImagesArchive : BlobContainers.ProjectImagesArchive;
            if (container != expected)
            {
                rejected.Add(line);
                continue;
            }
            entries.Add(new ArchivedOriginal(new ImageRef(kind, id), container, match.Groups["name"].Value));
        }
        return new BackfillLogEntries(entries, rejected);
    }
}
