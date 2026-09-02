using System.Security.Cryptography;
using System.Text;
using PrintLogApi.Mcp;
using PrintLogApi.Models;
using Xunit;

namespace PrintLogApi.IntegrationTests.Mcp;

/// <summary>
/// Proves that adding start/finish dates to create_project did not invalidate idempotency
/// records written before those fields existed.
/// </summary>
/// <remarks>
/// A round-trip test through the CURRENT serializer cannot show this: it passes whenever the
/// new implementation is merely self-consistent, even if its date-less bytes differ from what
/// the old one produced. So this reimplements the pre-change byte layout independently and
/// compares the two hashes directly. If someone later makes the date section unconditional —
/// the obvious "simplification" — every stored key for a date-less create would stop matching
/// and legitimate retries would start coming back as conflicts. This test fails first.
/// </remarks>
public class ProjectFingerprintCompatibilityTests
{
    /// <summary>
    /// The exact byte layout ComputeCreateProject used before the date fields were added:
    /// four length-prefixed strings then two ints, SHA-256, lowercase hex.
    /// </summary>
    private static string LegacyComputeCreateProject(
        string? name, string? reference, string? description, string? url,
        Project.ProjectStatus status, Project.ProjectViewStatus viewStatus)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            WriteStr(w, name);
            WriteStr(w, reference);
            WriteStr(w, description);
            WriteStr(w, url);
            w.Write((int)status);
            w.Write((int)viewStatus);
        }
        return Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();

        static void WriteStr(BinaryWriter w, string? v)
        {
            w.Write(v != null);
            if (v != null) w.Write(v);
        }
    }

    public static TheoryData<string?, string?, string?, string?, Project.ProjectStatus, Project.ProjectViewStatus> Cases() =>
        new()
        {
            { "Voron Build", null, null, null, Project.ProjectStatus.InProgress, Project.ProjectViewStatus.Private },
            { "Voron Build", "REF-1", "desc", "https://example.com", Project.ProjectStatus.Complete, Project.ProjectViewStatus.Public },
            { "", null, null, null, Project.ProjectStatus.OnHold, Project.ProjectViewStatus.Unlisted },
            { "unicode ✅ 日本語", null, "", null, Project.ProjectStatus.Cancelled, Project.ProjectViewStatus.Private },
        };

    [Theory]
    [MemberData(nameof(Cases))]
    public void DatelessCreate_HashesIdenticallyToThePreDateImplementation(
        string? name, string? reference, string? description, string? url,
        Project.ProjectStatus status, Project.ProjectViewStatus viewStatus)
    {
        var legacy = LegacyComputeCreateProject(name, reference, description, url, status, viewStatus);
        var current = McpRequestFingerprint.ComputeCreateProject(
            name, reference, description, url, status, viewStatus,
            startDate: null, finishDate: null);

        Assert.Equal(legacy, current);
    }

    [Fact]
    public void SupplyingADate_ChangesTheHash()
    {
        // The compatibility above must not come from ignoring the dates entirely: a key reused
        // with a DIFFERENT date has to be detectable as a conflict.
        var withoutDates = McpRequestFingerprint.ComputeCreateProject(
            "Voron Build", null, null, null,
            Project.ProjectStatus.InProgress, Project.ProjectViewStatus.Private, null, null);

        var withStart = McpRequestFingerprint.ComputeCreateProject(
            "Voron Build", null, null, null,
            Project.ProjectStatus.InProgress, Project.ProjectViewStatus.Private,
            new DateOnly(2026, 2, 1), null);

        var withFinish = McpRequestFingerprint.ComputeCreateProject(
            "Voron Build", null, null, null,
            Project.ProjectStatus.InProgress, Project.ProjectViewStatus.Private,
            null, new DateOnly(2026, 2, 1));

        Assert.NotEqual(withoutDates, withStart);
        Assert.NotEqual(withoutDates, withFinish);
        // A start-only and a finish-only request carrying the SAME day must not collide.
        Assert.NotEqual(withStart, withFinish);
    }
}
