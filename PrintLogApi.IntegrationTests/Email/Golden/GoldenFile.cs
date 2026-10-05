using System.Runtime.CompilerServices;
using Xunit;

namespace PrintLogApi.IntegrationTests.Email.Golden;

/// <summary>
/// Compares rendered output with a checked-in <c>*.approved.*</c> file next to this class. To
/// accept a change, run the failing test with <c>UPDATE_GOLDEN=1</c>: it rewrites the approved
/// file in the source tree and fails once, so an update is never silent. Review the diff, commit.
/// </summary>
public static class GoldenFile
{
    public static void AssertMatches(string actual, string approvedFileName, [CallerFilePath] string callerPath = "")
    {
        var normalized = actual.ReplaceLineEndings("\n");
        var outputCopy = Path.Combine(AppContext.BaseDirectory, "Email", "Golden", approvedFileName);

        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            // The source tree, not the output folder, is what gets committed.
            var sourceDir = Path.Combine(Path.GetDirectoryName(callerPath)!, "Golden");
            if (!Directory.Exists(sourceDir))
            {
                sourceDir = Path.GetDirectoryName(callerPath)!;
            }

            File.WriteAllText(Path.Combine(sourceDir, approvedFileName), normalized);
            Assert.Fail($"UPDATE_GOLDEN=1: wrote {approvedFileName}. Review it, then re-run without the variable.");
        }

        Assert.True(File.Exists(outputCopy), $"No approved file {approvedFileName}. Run with UPDATE_GOLDEN=1 to create it.");
        var approved = File.ReadAllText(outputCopy).ReplaceLineEndings("\n");
        Assert.Equal(approved, normalized);
    }
}
