using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrintLogApi.PrinterCatalog.Generation;

/// <summary>
/// <c>printer-catalog</c>: regenerates <c>printer-catalog.json</c> from Cura and OrcaSlicer
/// profiles (spec §5.1). A source without <c>--cura</c>/<c>--orca</c> is shallow-cloned at its
/// tag from <c>catalog-sources.json</c>, sparsely, so only the profile folders are fetched.
/// </summary>
internal static class PrinterCatalogCommand
{
    public const string Usage =
        "usage: printer-catalog [--cura <dir>] [--orca <dir>] [--sources <file>] [--overrides <file>] " +
        "[--aliases <file>] [--previous <file>] [--work <dir>] --out <file>";

    private const string CuraRepo = "Ultimaker/Cura";
    private const string OrcaRepo = "SoftFever/OrcaSlicer";

    private static readonly JsonSerializerOptions SourcesJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> RunAsync(string[] args)
    {
        string? cura = null, orca = null, sources = null, overrides = null, aliases = null, previous = null, work = null, output = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 >= args.Length)
            {
                return Fail($"unknown or incomplete argument: {args[i]}");
            }
            switch (args[i])
            {
                case "--cura": cura = args[++i]; break;
                case "--orca": orca = args[++i]; break;
                case "--sources": sources = args[++i]; break;
                case "--overrides": overrides = args[++i]; break;
                case "--aliases": aliases = args[++i]; break;
                case "--previous": previous = args[++i]; break;
                case "--work": work = args[++i]; break;
                case "--out": output = args[++i]; break;
                default: return Fail($"unknown or incomplete argument: {args[i]}");
            }
        }
        if (string.IsNullOrWhiteSpace(output))
        {
            return Fail("--out is required");
        }

        // The data files live together, so --sources locates the other two unless they are given.
        sources ??= FindDefaultSources();
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(sources))!;
        overrides ??= Path.Combine(dataDir, "catalog-overrides.json");
        aliases ??= Path.Combine(dataDir, "make-aliases.json");
        previous ??= File.Exists(output) ? output : null;
        foreach (var required in new[] { sources, overrides, aliases })
        {
            if (!File.Exists(required))
            {
                return Fail($"file not found: {required}");
            }
        }

        var declared = JsonSerializer.Deserialize<List<CatalogSource>>(await File.ReadAllTextAsync(sources), SourcesJson) ?? [];
        foreach (var (given, repo, flag) in new[] { (cura, CuraRepo, "--cura"), (orca, OrcaRepo, "--orca") })
        {
            if (given is not null && !declared.Any(s => s.Repo == repo))
            {
                Console.Error.WriteLine($"warning: {flag} is ignored: {sources} does not declare {repo}");
            }
        }
        var tempRoot = (cura is null || orca is null)
            ? Path.Combine(work ?? Path.GetTempPath(), $"printer-catalog-{Guid.NewGuid():N}")
            : null;
        try
        {
            var resolved = new List<CatalogSource>();
            foreach (var source in declared)
            {
                var (dir, sparsePaths) = source.Repo switch
                {
                    CuraRepo => (cura, new[] { "resources/definitions", "resources/extruders", "resources/variants" }),
                    OrcaRepo => (orca, new[] { "resources/profiles" }),
                    _ => throw new InvalidDataException($"{sources}: unknown repo {source.Repo}"),
                };
                if (dir is null)
                {
                    dir = Path.Combine(tempRoot!, source.Repo.Replace('/', '_'));
                    Console.Error.WriteLine($"cloning {source.Repo} at {source.Tag}");
                    await GitAsync(null, "clone", "--depth", "1", "--branch", source.Tag, "--filter=blob:none", "--sparse",
                        $"https://github.com/{source.Repo}.git", dir);
                    await GitAsync(dir, ["sparse-checkout", "set", .. sparsePaths]);
                }
                if (source.Repo == CuraRepo)
                {
                    cura = dir;
                }
                else
                {
                    orca = dir;
                }
                resolved.Add(source with { Commit = await TryGitAsync(dir, "rev-parse", "HEAD") ?? source.Commit });
            }

            var previousFile = previous is null ? null : CatalogFile.Deserialize(await File.ReadAllTextAsync(previous));
            var overridesJson = JsonNode.Parse(await File.ReadAllTextAsync(overrides)) as JsonObject
                ?? throw new InvalidDataException($"{overrides} must be a JSON object");
            var file = PrinterCatalogGenerator.Generate(new CatalogInputs
            {
                CuraDir = declared.Any(s => s.Repo == CuraRepo) ? cura : null,
                OrcaDir = declared.Any(s => s.Repo == OrcaRepo) ? orca : null,
                Sources = resolved,
                MakeAliases = CatalogNormalizer.LoadAliases(aliases),
                Overrides = overridesJson,
                Previous = previousFile,
                Clock = TimeProvider.System,
                Log = message => Console.Error.WriteLine($"warning: {message}"),
            });

            await File.WriteAllTextAsync(output, CatalogFile.Serialize(file), new UTF8Encoding(false));

            var before = previousFile?.Models.Select(m => m.Id).ToHashSet() ?? [];
            var after = file.Models.Select(m => m.Id).ToHashSet();
            // Hidden by an override is not gone from the sources; count it apart.
            var hidden = overridesJson.Where(o => o.Value is JsonObject entry && entry["hidden"]?.GetValueKind() == JsonValueKind.True)
                .Select(o => o.Key)
                .Where(id => file.Reserved.Any(r => r.Id == id))
                .ToHashSet();
            Console.WriteLine($"Wrote {output}");
            Console.WriteLine($"  Models   {file.Models.Count,6}");
            Console.WriteLine($"  Added    {after.Count(id => !before.Contains(id)),6}");
            Console.WriteLine($"  Gone     {before.Count(id => !after.Contains(id) && !hidden.Contains(id)),6}");
            Console.WriteLine($"  Hidden   {before.Count(id => hidden.Contains(id)),6}");
            Console.WriteLine($"  Version  {file.Version}");
            return 0;
        }
        catch (InvalidDataException e)
        {
            // A source that is missing or yields nothing, or a bad data file: say so and fail.
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
        finally
        {
            // A failed cleanup must not replace the exception that got us here.
            if (tempRoot is not null && Directory.Exists(tempRoot))
            {
                try
                {
                    DeleteTree(tempRoot);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"warning: could not delete {tempRoot}: {e.Message}");
                }
            }
        }
    }

    /// <summary>
    /// <c>PrintLogApi/PrinterCatalog/catalog-sources.json</c> in the nearest folder up from the
    /// working directory that has one, since <c>dotnet run</c> may start in the project folder.
    /// </summary>
    private static string FindDefaultSources()
    {
        var relative = Path.Combine("PrintLogApi", "PrinterCatalog", "catalog-sources.json");
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return relative;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static async Task<string> GitAsync(string? workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start git");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {(await stderr).Trim()}");
        }
        return (await stdout).Trim();
    }

    private static async Task<string?> TryGitAsync(string workingDirectory, params string[] args)
    {
        try
        {
            return await GitAsync(workingDirectory, args);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // Git marks pack files read-only, which Directory.Delete refuses on Windows.
    private static void DeleteTree(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }
}
