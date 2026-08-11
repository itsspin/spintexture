using System.Buffers.Binary;
using SpinTexture.Core.Archives;

namespace SpinTexture.SelfTest;

public static class ArchiveSelfTests
{
    public static async Task RunAsync(
        TextWriter? output = null,
        CancellationToken cancellationToken = default)
    {
        output ??= TextWriter.Null;
        AssertEqual(0x6859_EDB2u, PfsFormat.ComputeFilenameCrc("10backsn.bmp"), "known PFS CRC");
        AssertEqual(0x89FE_FC38u, PfsFormat.ComputeFilenameCrc("qeynos.wld"), "known PFS CRC");
        AssertEqual(
            PfsFormat.ComputeFilenameCrc("MiXeD.DDS"),
            PfsFormat.ComputeFilenameCrc("mixed.dds"),
            "case-insensitive PFS CRC");
        Assert(PfsArchive.IsSupportedExtension("zone.s3d"), "S3D path extension");
        Assert(PfsArchive.IsSupportedExtension(".eqg"), "bare EQG extension");
        Assert(PfsArchive.IsSupportedExtension("assets.pfs"), "PFS path extension");
        Assert(PfsArchive.IsSupportedExtension("legacy.pak"), "PAK path extension");

        var originalDds = CreateDds(width: 256, height: 128, "DXT1", seed: 7);
        var replacementDds = CreateDds(width: 512, height: 256, "DXT5", seed: 19);
        var notes = CreateDeterministicBytes(25_731, seed: 43);
        var targa = CreateTarga(width: 32, height: 16);
        var sourceItems = new[]
        {
            new PfsArchiveItem("10backsn.bmp", originalDds),
            new PfsArchiveItem("notes.txt", notes),
            new PfsArchiveItem("tiny.tga", targa),
            new PfsArchiveItem("zero.bin", ReadOnlyMemory<byte>.Empty)
        };
        var writeOptions = new PfsArchiveWriteOptions { ChunkSize = 257 };

        using var sourceStream = new MemoryStream();
        await PfsArchiveWriter.WriteAsync(
            sourceStream,
            sourceItems,
            writeOptions,
            cancellationToken).ConfigureAwait(false);
        var originalArchiveBytes = sourceStream.ToArray();

        sourceStream.Position = 0;
        await using var archive = await PfsArchive.OpenAsync(
            sourceStream,
            "synthetic.eqg",
            leaveOpen: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        AssertEqual(PfsFormat.Version2, archive.Version, "archive version");
        AssertEqual(sourceItems.Length, archive.Entries.Count, "entry count");

        var disguisedDds = archive.GetEntry("10BACKSN.BMP");
        AssertEqual(PfsContentKind.Dds, disguisedDds.Content.Kind, "content sniffing by payload");
        AssertEqual(256, disguisedDds.Content.Width, "DDS width");
        AssertEqual(128, disguisedDds.Content.Height, "DDS height");
        AssertEqual("DXT1", disguisedDds.Content.PixelFormat, "DDS pixel format");
        Assert(!disguisedDds.DeclaredExtensionMatchesContent, "misleading extension must be reported");

        var targaEntry = archive.GetEntry("tiny.tga");
        AssertEqual(PfsContentKind.Targa, targaEntry.Content.Kind, "TGA sniffing");
        AssertEqual(32, targaEntry.Content.Width, "TGA width");
        AssertEqual(16, targaEntry.Content.Height, "TGA height");

        var readOriginalDds = await archive.ReadEntryAsync("10backsn.bmp", cancellationToken).ConfigureAwait(false);
        var readNotes = await archive.ReadEntryAsync("notes.txt", cancellationToken).ConfigureAwait(false);
        var readEmpty = await archive.ReadEntryAsync("zero.bin", cancellationToken).ConfigureAwait(false);
        AssertSequenceEqual(originalDds, readOriginalDds, "DDS read");
        AssertSequenceEqual(notes, readNotes, "multi-chunk read");
        AssertSequenceEqual([], readEmpty, "zero-length read");

        using (var extracted = new MemoryStream())
        {
            await archive.ExtractEntryAsync("tiny.tga", extracted, cancellationToken).ConfigureAwait(false);
            AssertSequenceEqual(targa, extracted.ToArray(), "stream extraction");
        }

        using var noOpRebuild = new MemoryStream();
        await archive.RebuildAsync(
            noOpRebuild,
            options: writeOptions,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        AssertSequenceEqual(originalArchiveBytes, noOpRebuild.ToArray(), "no-op byte-identical rebuild");

        var originalNotesStoredBytes = originalArchiveBytes
            .AsSpan(checked((int)archive.GetEntry("notes.txt").StoredOffset), checked((int)archive.GetEntry("notes.txt").StoredLength))
            .ToArray();
        using var rebuiltStream = new MemoryStream();
        await archive.RebuildAsync(
            rebuiltStream,
            [new PfsArchiveReplacement("10backsn.bmp", replacementDds)],
            writeOptions,
            cancellationToken).ConfigureAwait(false);

        rebuiltStream.Position = 0;
        await using var rebuiltArchive = await PfsArchive.OpenAsync(
            rebuiltStream,
            "rebuilt.s3d",
            leaveOpen: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var readReplacement = await rebuiltArchive.ReadEntryAsync("10backsn.bmp", cancellationToken)
            .ConfigureAwait(false);
        var readPreservedNotes = await rebuiltArchive.ReadEntryAsync("notes.txt", cancellationToken)
            .ConfigureAwait(false);
        AssertSequenceEqual(replacementDds, readReplacement, "replacement payload");
        AssertSequenceEqual(notes, readPreservedNotes, "preserved payload");
        AssertEqual(512, rebuiltArchive.GetEntry("10backsn.bmp").Content.Width, "replacement metadata");
        AssertEqual("DXT5", rebuiltArchive.GetEntry("10backsn.bmp").Content.PixelFormat, "replacement format");

        var rebuiltBytes = rebuiltStream.ToArray();
        var rebuiltNotes = rebuiltArchive.GetEntry("notes.txt");
        var rebuiltNotesStoredBytes = rebuiltBytes
            .AsSpan(checked((int)rebuiltNotes.StoredOffset), checked((int)rebuiltNotes.StoredLength))
            .ToArray();
        AssertSequenceEqual(
            originalNotesStoredBytes,
            rebuiltNotesStoredBytes,
            "unchanged compressed block bytes");

        var fileReplacementDirectory = Path.Combine(
            Path.GetTempPath(),
            $"SpinTexture-archive-replacement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fileReplacementDirectory);
        try
        {
            var replacementPath = Path.Combine(fileReplacementDirectory, "replacement.dds");
            await File.WriteAllBytesAsync(
                replacementPath,
                replacementDds,
                cancellationToken).ConfigureAwait(false);
            var fileReplacement = PfsArchiveReplacement.FromFile("10backsn.bmp", replacementPath);
            Assert(fileReplacement.IsFileBacked, "file-backed replacement marker");
            AssertEqual(
                Path.GetFullPath(replacementPath),
                fileReplacement.SourceFilePath!,
                "file-backed replacement source path");

            using var fileBackedRebuild = new MemoryStream();
            await archive.RebuildAsync(
                fileBackedRebuild,
                [fileReplacement],
                writeOptions,
                cancellationToken).ConfigureAwait(false);
            AssertSequenceEqual(
                rebuiltStream.ToArray(),
                fileBackedRebuild.ToArray(),
                "file-backed and in-memory replacement equivalence");

            var missingReplacementPath = Path.Combine(fileReplacementDirectory, "missing.dds");
            var destinationSentinel = CreateDeterministicBytes(97, seed: 61);
            using var invalidReplacementDestination = new MemoryStream();
            await invalidReplacementDestination.WriteAsync(
                destinationSentinel,
                cancellationToken).ConfigureAwait(false);
            await AssertThrowsAsync<FileNotFoundException>(
                () => archive.RebuildAsync(
                    invalidReplacementDestination,
                    [PfsArchiveReplacement.FromFile("10backsn.bmp", missingReplacementPath)],
                    writeOptions,
                    cancellationToken),
                "missing replacement source-file rejection").ConfigureAwait(false);
            AssertSequenceEqual(
                destinationSentinel,
                invalidReplacementDestination.ToArray(),
                "replacement source validation precedes destination mutation");
        }
        finally
        {
            Directory.Delete(fileReplacementDirectory, recursive: true);
        }

        var badMagic = originalArchiveBytes.ToArray();
        badMagic[sizeof(uint)] ^= 0xFF;
        await AssertThrowsAsync<PfsArchiveException>(
            async () =>
            {
                await using var invalid = await PfsArchive.OpenAsync(
                    new MemoryStream(badMagic),
                    "bad-magic.pfs",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            },
            "bad magic rejection").ConfigureAwait(false);

        using var canceledStream = new MemoryStream(originalArchiveBytes);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(
            async () =>
            {
                await using var canceled = await PfsArchive.OpenAsync(
                    canceledStream,
                    "canceled.pfs",
                    leaveOpen: true,
                    cancellationToken: cancellation.Token).ConfigureAwait(false);
            },
            "cancellation").ConfigureAwait(false);

        await output.WriteLineAsync("Archive self-tests passed.").ConfigureAwait(false);
    }

    public static async Task RunReadOnlySmokeAsync(
        string archivePath,
        TextWriter? output = null,
        CancellationToken cancellationToken = default)
    {
        output ??= TextWriter.Null;
        await using var source = await PfsArchive.OpenAsync(archivePath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        Assert(source.Entries.Count > 0, "real archive should contain entries");

        var samples = source.Entries
            .Where(entry => entry.UncompressedSize <= 64 * 1024 * 1024)
            .Take(3)
            .Concat(source.Entries.Where(entry => entry.UncompressedSize <= 64 * 1024 * 1024).TakeLast(3))
            .DistinctBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedPayloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples)
        {
            expectedPayloads.Add(
                sample.Name,
                await source.ReadEntryAsync(sample.Name, cancellationToken).ConfigureAwait(false));
        }

        using var rebuiltStream = new MemoryStream();
        await source.RebuildAsync(rebuiltStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        rebuiltStream.Position = 0;
        await using var rebuilt = await PfsArchive.OpenAsync(
            rebuiltStream,
            $"rebuilt-{Path.GetFileName(archivePath)}",
            leaveOpen: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        AssertEqual(source.Entries.Count, rebuilt.Entries.Count, "real archive round-trip entry count");
        foreach (var expected in expectedPayloads)
        {
            var actual = await rebuilt.ReadEntryAsync(expected.Key, cancellationToken).ConfigureAwait(false);
            AssertSequenceEqual(
                expected.Value,
                actual,
                $"real archive payload '{expected.Key}'");
        }

        await output.WriteLineAsync(
            $"Read-only archive smoke test passed for {archivePath} ({source.Entries.Count} entries).")
            .ConfigureAwait(false);
    }

#if ARCHIVE_TEST_RUNNER
    [System.Runtime.CompilerServices.ModuleInitializer]
    public static void RunAsModuleInitializer()
    {
        var paths = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var runner = new Thread(() =>
        {
            try
            {
                RunAsync(Console.Out).GetAwaiter().GetResult();
                foreach (var path in paths)
                {
                    RunReadOnlySmokeAsync(path, Console.Out).GetAwaiter().GetResult();
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                Environment.ExitCode = 1;
            }
        })
        {
            IsBackground = false,
            Name = "SpinTexture archive self-tests"
        };
        runner.Start();
    }
#endif

    private static byte[] CreateDds(int width, int height, string fourCc, byte seed)
    {
        var bytes = CreateDeterministicBytes(512, seed);
        "DDS "u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x0002_1007);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), 0x4);
        var fourCcBytes = System.Text.Encoding.ASCII.GetBytes(fourCc);
        fourCcBytes.CopyTo(bytes, 84);
        return bytes;
    }

    private static byte[] CreateTarga(int width, int height)
    {
        var bytes = new byte[18 + (width * height * 4)];
        bytes[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), checked((ushort)width));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), checked((ushort)height));
        bytes[16] = 32;
        bytes[17] = 8;
        return bytes;
    }

    private static byte[] CreateDeterministicBytes(int length, byte seed)
    {
        var bytes = new byte[length];
        var state = (uint)seed + 1;
        for (var index = 0; index < bytes.Length; index++)
        {
            state = (state * 1_664_525) + 1_013_904_223;
            bytes[index] = (byte)(state >> 24);
        }

        return bytes;
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action, string description)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Self-test failed: {description} did not throw {typeof(TException).Name}.");
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Self-test failed: {description}.");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string description)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Self-test failed: {description}; expected '{expected}', got '{actual}'.");
        }
    }

    private static void AssertSequenceEqual(
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> actual,
        string description)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException($"Self-test failed: {description} differs.");
        }
    }
}
