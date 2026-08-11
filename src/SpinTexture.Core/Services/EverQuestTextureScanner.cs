using System.Collections.Concurrent;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Models;

namespace SpinTexture.Core.Services;

public sealed class EverQuestTextureScanner
{
    private static readonly HashSet<string> LooseTextureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dds", ".tga", ".bmp", ".png", ".jpg", ".jpeg"
    };

    private static readonly HashSet<string> IgnoredTopLevelDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "SpinTextureProject", "SpinTextureData", "Staging", "Backups", "Cache", "publish",
        "vendor", ".dotnet", ".nuget", ".git"
    };

    public async Task<ScanSummary> AnalyzeAsync(
        string installPath,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var validation = EverQuestInstall.Validate(installPath);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, validation.Errors));
        }

        var root = validation.NormalizedPath;
        var archives = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(path => EverQuestInstall.IsPfsArchiveExtension(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var warnings = new ConcurrentQueue<string>(validation.Warnings);
        long packedTextures = 0;
        long completedArchives = 0;
        long sourceBytes = 0;

        foreach (var archivePath in archives)
        {
            sourceBytes += new FileInfo(archivePath).Length;
        }

        progress?.Report(new ProgressUpdate(
            "Analyze",
            "Indexing PFS archives by payload signature.",
            0,
            archives.Length,
            root));

        await Parallel.ForEachAsync(
            archives,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8)
            },
            async (archivePath, token) =>
            {
                try
                {
                    await using var archive = await PfsArchive.OpenAsync(
                        archivePath,
                        cancellationToken: token).ConfigureAwait(false);
                    Interlocked.Add(
                        ref packedTextures,
                        archive.Entries.LongCount(entry => entry.IsTexture));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException)
                {
                    if (warnings.Count < 25)
                    {
                        warnings.Enqueue($"Skipped {Path.GetFileName(archivePath)}: {exception.Message}");
                    }
                }
                finally
                {
                    var completed = Interlocked.Increment(ref completedArchives);
                    if (completed == archives.Length || completed % 25 == 0)
                    {
                        progress?.Report(new ProgressUpdate(
                            "Analyze",
                            $"Indexed {completed:N0} of {archives.Length:N0} archives.",
                            checked((int)Math.Min(completed, int.MaxValue)),
                            archives.Length,
                            Path.GetFileName(archivePath)));
                    }
                }
            }).ConfigureAwait(false);

        var looseTextureCount = 0;
        foreach (var filePath in EnumerateLooseTextures(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            looseTextureCount++;
            try
            {
                sourceBytes += new FileInfo(filePath).Length;
            }
            catch (IOException exception)
            {
                if (warnings.Count < 25)
                {
                    warnings.Enqueue($"Could not read {Path.GetFileName(filePath)}: {exception.Message}");
                }
            }
        }

        var zones = ZoneCatalog.Discover(root).Select(zone => zone.Name).ToArray();
        progress?.Report(new ProgressUpdate(
            "Analyze",
            $"Found {packedTextures:N0} packed and {looseTextureCount:N0} loose textures.",
            archives.Length,
            archives.Length,
            "Read-only inventory complete"));

        return new ScanSummary(
            root,
            archives.Length,
            checked((int)Math.Min(packedTextures, int.MaxValue)),
            looseTextureCount,
            sourceBytes,
            zones,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).Take(25).ToArray());
    }

    public static IEnumerable<string> EnumerateLooseTextures(string installPath)
    {
        var root = Path.GetFullPath(installPath);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var filePath in Directory.EnumerateFiles(root, "*", options))
        {
            var relativePath = Path.GetRelativePath(root, filePath);
            var firstSegment = relativePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                2,
                StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstSegment is not null && IgnoredTopLevelDirectories.Contains(firstSegment))
            {
                continue;
            }

            if (LooseTextureExtensions.Contains(Path.GetExtension(filePath)))
            {
                yield return filePath;
            }
        }
    }
}
