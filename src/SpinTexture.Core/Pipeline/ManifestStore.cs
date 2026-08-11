using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpinTexture.Core.Pipeline;

public sealed class ManifestStore
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public Task WriteBuildManifestAsync(
        string path,
        BuildManifest manifest,
        CancellationToken cancellationToken = default) =>
        WriteAsync(path, manifest, cancellationToken);

    public Task WriteInstallManifestAsync(
        string path,
        InstallManifest manifest,
        CancellationToken cancellationToken = default) =>
        WriteAsync(path, manifest, cancellationToken);

    public async Task<BuildManifest> ReadBuildManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadAsync<BuildManifest>(path, cancellationToken).ConfigureAwait(false);
        if (manifest.SchemaVersion != BuildManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported build manifest schema {manifest.SchemaVersion}.");
        }

        return manifest;
    }

    public async Task<InstallManifest> ReadInstallManifestAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ReadAsync<InstallManifest>(path, cancellationToken).ConfigureAwait(false);
        if (manifest.SchemaVersion != InstallManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported install manifest schema {manifest.SchemaVersion}.");
        }

        return manifest;
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);
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
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken).ConfigureAwait(false);
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

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken).ConfigureAwait(false);
        return value ?? throw new InvalidDataException($"Manifest {path} is empty or invalid.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
