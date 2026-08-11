using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpinTexture.Core.Models;
using SpinTexture.Core.Pipeline;

namespace SpinTexture.Core.Services;

public interface IStagedPackPayloadMaterializer
{
    Task<StagedPackMaterializationKind> MaterializeAsync(
        string sourcePath,
        string destinationPath,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken = default);
}

internal interface IVerifiedStagedPackPayloadMaterializer
{
    Task<StagedPackMaterializationKind> MaterializeVerifiedAsync(
        string sourcePath,
        string destinationPath,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses a same-volume hard link when Windows supports it, otherwise makes an
/// atomic, SHA-256-verified copy. Neither route writes to the constituent pack.
/// </summary>
public sealed class HardLinkOrCopyStagedPackPayloadMaterializer
    : IStagedPackPayloadMaterializer, IVerifiedStagedPackPayloadMaterializer
{
    private readonly Func<string, string, bool> tryCreateHardLink;

    public HardLinkOrCopyStagedPackPayloadMaterializer()
        : this(TryCreateWindowsHardLink)
    {
    }

    internal HardLinkOrCopyStagedPackPayloadMaterializer(
        Func<string, string, bool> tryCreateHardLink)
    {
        this.tryCreateHardLink = tryCreateHardLink
            ?? throw new ArgumentNullException(nameof(tryCreateHardLink));
    }

    public async Task<StagedPackMaterializationKind> MaterializeAsync(
        string sourcePath,
        string destinationPath,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken = default) =>
        await MaterializeCoreAsync(
                sourcePath,
                destinationPath,
                expectedLength,
                expectedSha256,
                sourceAlreadyVerified: false,
                cancellationToken)
            .ConfigureAwait(false);

    Task<StagedPackMaterializationKind> IVerifiedStagedPackPayloadMaterializer.MaterializeVerifiedAsync(
        string sourcePath,
        string destinationPath,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken) =>
        MaterializeCoreAsync(
            sourcePath,
            destinationPath,
            expectedLength,
            expectedSha256,
            sourceAlreadyVerified: true,
            cancellationToken);

    private async Task<StagedPackMaterializationKind> MaterializeCoreAsync(
        string sourcePath,
        string destinationPath,
        long expectedLength,
        string expectedSha256,
        bool sourceAlreadyVerified,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException($"Composite payload destination already exists: {destinationPath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        var linked = false;
        try
        {
            linked = tryCreateHardLink(
                Path.GetFullPath(destinationPath),
                Path.GetFullPath(sourcePath));
        }
        catch (Exception exception) when (exception is
                                           IOException or
                                           UnauthorizedAccessException or
                                           NotSupportedException)
        {
            linked = false;
        }

        if (linked)
        {
            try
            {
                if (sourceAlreadyVerified)
                {
                    var destination = new FileInfo(destinationPath);
                    destination.Refresh();
                    if (!destination.Exists || destination.Length != expectedLength)
                    {
                        throw new InvalidDataException(
                            "The new hard link does not match the verified source length.");
                    }
                }
                else
                {
                    await FileIntegrity.EnsureMatchesAsync(
                            destinationPath,
                            expectedLength,
                            expectedSha256,
                            "Hard-linked composite payload",
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return StagedPackMaterializationKind.HardLink;
            }
            catch
            {
                AtomicFile.TryDelete(destinationPath);
                throw;
            }
        }

        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            throw new IOException(
                $"Hard-link fallback left an unexpected destination: {destinationPath}");
        }

        await AtomicFile.CopyAndReplaceAsync(
                sourcePath,
                destinationPath,
                expectedLength,
                expectedSha256,
                cancellationToken)
            .ConfigureAwait(false);
        return StagedPackMaterializationKind.VerifiedCopy;
    }

    private static bool TryCreateWindowsHardLink(string destinationPath, string sourcePath) =>
        OperatingSystem.IsWindows()
        && NativeMethods.CreateHardLink(destinationPath, sourcePath, IntPtr.Zero);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateHardLink(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);
    }
}

/// <summary>
/// Combines completed packs without rerunning an artifact builder. Composition
/// operates at complete-install-artifact granularity: conflicting outputs for
/// one archive are rejected rather than silently choosing a winner.
/// </summary>
public sealed class StagedPackComposer
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly StagedPackCatalogService catalog;
    private readonly ManifestStore manifestStore;
    private readonly IStagedPackPayloadMaterializer materializer;

    public StagedPackComposer(
        StagedPackCatalogService? catalog = null,
        ManifestStore? manifestStore = null,
        IStagedPackPayloadMaterializer? materializer = null)
    {
        this.manifestStore = manifestStore ?? new ManifestStore();
        this.catalog = catalog ?? new StagedPackCatalogService(this.manifestStore);
        this.materializer = materializer ?? new HardLinkOrCopyStagedPackPayloadMaterializer();
    }

    public async Task<StagedPackCompositionPlan> PlanAsync(
        ProjectPaths paths,
        IReadOnlyList<string> manifestPaths,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifestPaths);
        if (manifestPaths.Count == 0)
        {
            throw new ArgumentException(
                "Select at least one completed staged pack to compose.",
                nameof(manifestPaths));
        }

        var uniqueManifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new List<StagedPackInfo>(manifestPaths.Count);
        for (var index = 0; index < manifestPaths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(manifestPaths[index]);
            if (!uniqueManifestPaths.Add(fullPath))
            {
                continue;
            }

            progress?.Report(new ProgressUpdate(
                "Compose preflight",
                "Hash-verifying a selected staged pack.",
                index,
                manifestPaths.Count,
                Path.GetFileName(Path.GetDirectoryName(fullPath))));
            var info = await catalog.InspectAsync(
                    paths,
                    fullPath,
                    StagedPackVerificationMode.Exact,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!info.IsReady || info.Manifest is null || info.ManifestFingerprint is null)
            {
                throw new InvalidDataException(
                    $"Staged pack '{info.CandidateBuildId}' is not ready for composition: {info.Summary}");
            }

            components.Add(info);
        }

        if (components.Count == 0)
        {
            throw new InvalidOperationException("No unique completed staged packs were selected.");
        }

        components = components
            .OrderBy(info => info.Manifest!.BuildId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(info => info.ManifestPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var merged = new Dictionary<string, MutableCompositionEntry>(
            StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<StagedPackCompositionConflict>();
        foreach (var component in components)
        {
            var manifest = component.Manifest!;
            foreach (var artifact in component.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedEntry = artifact.Entry with
                {
                    RelativeInstallPath = artifact.CanonicalRelativeInstallPath
                };
                var origin = new StagedPackCompositionOrigin(
                    manifest.BuildId,
                    component.ManifestPath,
                    artifact.PayloadPath);
                if (!merged.TryGetValue(
                        artifact.CanonicalRelativeInstallPath,
                        out var existing))
                {
                    merged.Add(
                        artifact.CanonicalRelativeInstallPath,
                        new MutableCompositionEntry(normalizedEntry, [origin]));
                    continue;
                }

                if (existing.Entry.SourceLength != normalizedEntry.SourceLength
                    || !existing.Entry.SourceSha256.Equals(
                        normalizedEntry.SourceSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add(new StagedPackCompositionConflict(
                        artifact.CanonicalRelativeInstallPath,
                        StagedPackCompositionConflictKind.SourceMismatch,
                        existing.Origins[0].BuildId,
                        manifest.BuildId,
                        "The packs were built from different source bytes for the same install artifact."));
                    continue;
                }

                if (existing.Entry.StagedLength != normalizedEntry.StagedLength
                    || !existing.Entry.StagedSha256.Equals(
                        normalizedEntry.StagedSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add(new StagedPackCompositionConflict(
                        artifact.CanonicalRelativeInstallPath,
                        StagedPackCompositionConflictKind.OutputMismatch,
                        existing.Origins[0].BuildId,
                        manifest.BuildId,
                        "The packs contain different enhanced outputs for the same complete install artifact."));
                    continue;
                }

                existing.Origins.Add(origin);
            }
        }

        var entries = merged.Values
            .OrderBy(item => item.Entry.RelativeInstallPath, StringComparer.OrdinalIgnoreCase)
            .Select(item => new StagedPackCompositionEntry(
                item.Entry,
                item.Origins
                    .OrderBy(origin => origin.BuildId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(origin => origin.ManifestPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToArray();
        return new StagedPackCompositionPlan(
            components.AsReadOnly(),
            entries,
            conflicts
                .OrderBy(conflict => conflict.RelativeInstallPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(conflict => conflict.Kind)
                .ToArray());
    }

    public async Task<StagedPackCompositionResult> ComposeAsync(
        ProjectPaths paths,
        IReadOnlyList<string> manifestPaths,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default,
        string? compositionId = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureWorkspaceDirectories();
        using var transactionLock = TransactionLock.Acquire(paths.WorkspacePath);
        using var stagingLock = StagingLibraryLock.Acquire(paths.WorkspacePath);
        var plan = await PlanAsync(
                paths,
                manifestPaths,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (!plan.CanCompose)
        {
            var summary = string.Join(
                "; ",
                plan.Conflicts.Take(5).Select(conflict =>
                    $"{conflict.RelativeInstallPath}: {conflict.Summary}"));
            throw new StagedPackCompositionException(
                $"The selected packs cannot be composed safely. {summary}",
                plan.Conflicts);
        }

        var id = PathGuard.ValidateIdentifier(compositionId, "composite");
        var buildDirectory = PathGuard.ResolveUnderRoot(paths.StagingPath, id);
        if (Directory.Exists(buildDirectory) || File.Exists(buildDirectory))
        {
            throw new IOException($"Staged composition already exists: {buildDirectory}");
        }

        var payloadDirectory = Path.Combine(buildDirectory, "payload");
        Directory.CreateDirectory(payloadDirectory);
        try
        {
            var linkedCount = 0;
            var copiedCount = 0;
            var verifiedArtifacts = plan.Components
                .SelectMany(component => component.Artifacts)
                .Where(artifact => artifact.State == StagedPackArtifactValidationState.HashVerified
                                   && artifact.VerifiedLastWriteTimeUtcTicks is > 0)
                .ToDictionary(
                    artifact => Path.GetFullPath(artifact.PayloadPath),
                    StringComparer.OrdinalIgnoreCase);
            var hardLinkedSources = new List<(string SourcePath, string DestinationPath, long Length, long LastWriteTicks)>();
            for (var index = 0; index < plan.Entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = plan.Entries[index];
                progress?.Report(new ProgressUpdate(
                    "Compose",
                    "Materializing a verified staged artifact without rerunning AI.",
                    index,
                    plan.Entries.Count,
                    item.Entry.RelativeInstallPath));
                var fullSourcePath = Path.GetFullPath(item.SourcePayloadPath);
                if (!verifiedArtifacts.TryGetValue(fullSourcePath, out var verified))
                {
                    throw new InvalidDataException(
                        $"The constituent payload has no stable exact verification: {item.Entry.RelativeInstallPath}");
                }

                var sourceInfo = new FileInfo(fullSourcePath);
                sourceInfo.Refresh();
                if (!sourceInfo.Exists
                    || sourceInfo.Length != item.Entry.StagedLength
                    || sourceInfo.LastWriteTimeUtc.Ticks != verified.VerifiedLastWriteTimeUtcTicks)
                {
                    throw new InvalidDataException(
                        $"The constituent payload changed after exact verification: {item.Entry.RelativeInstallPath}");
                }

                var destinationPath = PathGuard.ResolveUnderRoot(
                    payloadDirectory,
                    item.Entry.RelativeInstallPath);
                var kind = materializer is IVerifiedStagedPackPayloadMaterializer verifiedMaterializer
                    ? await verifiedMaterializer.MaterializeVerifiedAsync(
                            item.SourcePayloadPath,
                            destinationPath,
                            item.Entry.StagedLength,
                            item.Entry.StagedSha256,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await materializer.MaterializeAsync(
                            item.SourcePayloadPath,
                            destinationPath,
                            item.Entry.StagedLength,
                            item.Entry.StagedSha256,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (kind == StagedPackMaterializationKind.HardLink)
                {
                    linkedCount++;
                    hardLinkedSources.Add((
                        fullSourcePath,
                        destinationPath,
                        item.Entry.StagedLength,
                        sourceInfo.LastWriteTimeUtc.Ticks));
                }
                else
                {
                    copiedCount++;
                }
            }

            foreach (var component in plan.Components)
            {
                var expected = component.ManifestFingerprint!;
                await FileIntegrity.EnsureMatchesAsync(
                        component.ManifestPath,
                        expected.Length,
                        expected.Sha256,
                        "Constituent staged-pack manifest after composition",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var linked in hardLinkedSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = new FileInfo(linked.SourcePath);
                var destination = new FileInfo(linked.DestinationPath);
                source.Refresh();
                destination.Refresh();
                if (!source.Exists
                    || !destination.Exists
                    || source.Length != linked.Length
                    || destination.Length != linked.Length
                    || source.LastWriteTimeUtc.Ticks != linked.LastWriteTicks
                    || destination.LastWriteTimeUtc.Ticks != linked.LastWriteTicks)
                {
                    throw new InvalidDataException(
                        "A hard-linked constituent payload changed before the composite manifest commit.");
                }
            }

            var createdUtc = DateTimeOffset.UtcNow;
            var compatibilityOptions = plan.Components[0].Manifest!.Options;
            var composition = new StagedPackCompositionDocument(
                StagedPackCompositionDocument.CurrentSchemaVersion,
                id,
                createdUtc,
                Path.GetFullPath(paths.InstallPath),
                compatibilityOptions,
                plan.Components.Select(component =>
                {
                    var manifest = component.Manifest!;
                    var fingerprint = component.ManifestFingerprint!;
                    return new StagedPackCompositionComponent(
                        manifest.BuildId,
                        Path.GetRelativePath(paths.StagingPath, component.ManifestPath),
                        fingerprint.Length,
                        fingerprint.Sha256,
                        manifest.CreatedUtc,
                        manifest.Options,
                        manifest.Entries.Count);
                }).ToArray(),
                plan.Entries.Select(entry =>
                    new StagedPackCompositionEntryProvenance(
                        entry.Entry.RelativeInstallPath,
                        entry.Origins
                            .Select(origin => origin.BuildId)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(buildId => buildId, StringComparer.OrdinalIgnoreCase)
                            .ToArray())).ToArray());
            var compositionDocumentPath = Path.Combine(buildDirectory, "composition.json");
            await WriteJsonAtomicallyAsync(
                    compositionDocumentPath,
                    composition,
                    cancellationToken)
                .ConfigureAwait(false);

            // BuildManifest schema 1 is intentionally retained so the existing
            // transaction installer can consume a composition. Options is a
            // compatibility value only; composition.json carries every
            // component's real (possibly mixed) options and provenance.
            var manifest = new BuildManifest(
                BuildManifest.CurrentSchemaVersion,
                id,
                createdUtc,
                Path.GetFullPath(paths.InstallPath),
                compatibilityOptions,
                plan.Entries.Select(entry => entry.Entry).ToArray());
            var manifestPath = Path.Combine(buildDirectory, "manifest.json");
            await manifestStore.WriteBuildManifestAsync(
                    manifestPath,
                    manifest,
                    cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new ProgressUpdate(
                "Compose",
                $"Composite pack is ready: {plan.Entries.Count:N0} artifact(s).",
                plan.Entries.Count,
                plan.Entries.Count,
                id));
            return new StagedPackCompositionResult(
                id,
                buildDirectory,
                manifestPath,
                compositionDocumentPath,
                manifest,
                composition,
                linkedCount,
                copiedCount);
        }
        catch
        {
            TryDeleteOwnedComposition(paths.StagingPath, buildDirectory);
            throw;
        }
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = AtomicFile.CreateTemporarySiblingPath(fullPath);
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        value,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            AtomicFile.CommitTemporaryFile(temporaryPath, fullPath);
        }
        catch
        {
            AtomicFile.TryDelete(temporaryPath);
            throw;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static void TryDeleteOwnedComposition(string stagingRoot, string buildDirectory)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(buildDirectory));
            var parent = Path.GetDirectoryName(target);
            if (parent is null
                || !PathGuard.SamePath(root, parent)
                || !Directory.Exists(target)
                || ContainsReparsePoint(target))
            {
                return;
            }

            Directory.Delete(target, recursive: true);
        }
        catch (Exception exception) when (exception is
                                           IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            // An incomplete directory has no manifest commit marker, so leaving
            // it behind is safe and the catalog will not offer it for install.
        }
    }

    private static bool ContainsReparsePoint(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            foreach (var child in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(child);
                }
            }
        }

        return false;
    }

    private sealed class MutableCompositionEntry
    {
        public MutableCompositionEntry(
            BuildManifestEntry entry,
            List<StagedPackCompositionOrigin> origins)
        {
            Entry = entry;
            Origins = origins;
        }

        public BuildManifestEntry Entry { get; }
        public List<StagedPackCompositionOrigin> Origins { get; }
    }
}
