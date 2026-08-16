using System.Buffers.Binary;
using SpinTexture.Core;
using SpinTexture.Core.Archives;
using SpinTexture.Core.Services;
using SpinTexture.Core.Textures;

namespace SpinTexture.SelfTest;

/// <summary>
/// Covers the managed texture decoding and staged-payload resolution behind
/// the review gallery's on-demand before/after rendering.
/// </summary>
internal static class PreviewOnDemandSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        TestIndexedBmpDecode();
        TestTopDownIndexedBmpDecode();
        TestTargaDecode();
        TestUnsupportedContainersReturnNull();
        await TestStagedPreviewResolutionAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TestIndexedBmpDecode()
    {
        // 4x2 bottom-up indexed bitmap with a two-color palette.
        var bmp = CreateIndexedBmp(
            width: 4,
            height: 2,
            topDown: false,
            palette: [(10, 20, 30), (200, 150, 100)],
            indexRowsTopFirst:
            [
                [0, 0, 1, 1],
                [1, 1, 0, 0]
            ]);
        var decoded = ClassicTextureDecoder.TryDecode(bmp)
            ?? throw new InvalidOperationException("Self-test failed: indexed BMP did not decode.");
        AssertEqual(4, decoded.Width, "indexed BMP width");
        AssertEqual(2, decoded.Height, "indexed BMP height");
        AssertPixel(decoded, x: 0, y: 0, blue: 10, green: 20, red: 30, alpha: 255, "indexed top-left");
        AssertPixel(decoded, x: 3, y: 0, blue: 200, green: 150, red: 100, alpha: 255, "indexed top-right");
        AssertPixel(decoded, x: 0, y: 1, blue: 200, green: 150, red: 100, alpha: 255, "indexed bottom-left");
    }

    private static void TestTopDownIndexedBmpDecode()
    {
        var bmp = CreateIndexedBmp(
            width: 4,
            height: 2,
            topDown: true,
            palette: [(1, 2, 3), (4, 5, 6)],
            indexRowsTopFirst:
            [
                [0, 1, 0, 1],
                [1, 0, 1, 0]
            ]);
        var decoded = ClassicTextureDecoder.TryDecode(bmp)
            ?? throw new InvalidOperationException("Self-test failed: top-down indexed BMP did not decode.");
        AssertPixel(decoded, x: 1, y: 0, blue: 4, green: 5, red: 6, alpha: 255, "top-down indexed (1,0)");
        AssertPixel(decoded, x: 1, y: 1, blue: 1, green: 2, red: 3, alpha: 255, "top-down indexed (1,1)");
    }

    private static void TestTargaDecode()
    {
        // 2x2 bottom-up 32-bit targa: bottom row is written first in the file.
        var tga = new byte[18 + 16];
        tga[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(12, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(tga.AsSpan(14, 2), 2);
        tga[16] = 32;
        tga[17] = 8;
        // bottom row: (B1,G1,R1,A1)(B2,...)
        tga[18] = 11; tga[19] = 12; tga[20] = 13; tga[21] = 14;
        tga[22] = 21; tga[23] = 22; tga[24] = 23; tga[25] = 24;
        // top row
        tga[26] = 31; tga[27] = 32; tga[28] = 33; tga[29] = 34;
        tga[30] = 41; tga[31] = 42; tga[32] = 43; tga[33] = 44;
        var decoded = ClassicTextureDecoder.TryDecode(tga)
            ?? throw new InvalidOperationException("Self-test failed: 32-bit targa did not decode.");
        AssertEqual(2, decoded.Width, "targa width");
        AssertEqual(2, decoded.Height, "targa height");
        AssertPixel(decoded, x: 0, y: 0, blue: 31, green: 32, red: 33, alpha: 34, "targa top-left");
        AssertPixel(decoded, x: 1, y: 1, blue: 21, green: 22, red: 23, alpha: 24, "targa bottom-right");
    }

    private static void TestUnsupportedContainersReturnNull()
    {
        Assert(
            ClassicTextureDecoder.TryDecode("DDS |compressed-block-data"u8) is null,
            "DDS payload must not decode in the managed preview path");
        Assert(
            ClassicTextureDecoder.TryDecode([]) is null,
            "empty payload must not decode");
        Assert(
            ClassicTextureDecoder.TryDecode("BM"u8) is null,
            "truncated BMP must not decode");
    }

    private static async Task TestStagedPreviewResolutionAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-preview-on-demand-{Guid.NewGuid():N}");
        try
        {
            var paths = new ProjectPaths(
                Path.Combine(root, "EverQuest"),
                Path.Combine(root, "Workspace"));
            var buildDirectory = Path.Combine(paths.StagingPath, "builds", "preview-on-demand");
            var payloadDirectory = Path.Combine(buildDirectory, "payload");
            Directory.CreateDirectory(payloadDirectory);
            var wallBmp = CreateIndexedBmp(
                width: 16,
                height: 16,
                topDown: false,
                palette: [(64, 72, 80), (96, 104, 112)],
                indexRowsTopFirst: Enumerable.Range(0, 16)
                    .Select(row => Enumerable.Range(0, 16)
                        .Select(column => (byte)((row + column) % 2))
                        .ToArray())
                    .ToArray());
            await using (var stream = new FileStream(
                Path.Combine(payloadDirectory, "zone.s3d"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await PfsArchiveWriter.WriteAsync(
                        stream,
                        [
                            new PfsArchiveItem("wall.bmp", wallBmp),
                            new PfsArchiveItem("crystal.dds", "DDS |synthetic"u8.ToArray())
                        ],
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            var previewManifestPath = Path.Combine(
                buildDirectory,
                "previews",
                "preview-manifest.json");
            var workflow = new TexturePackWorkflow(clientClosedGuard: () => { });

            var staged = await workflow.LoadStagedTexturePreviewAsync(
                    paths,
                    previewManifestPath,
                    "zone.s3d",
                    "wall.bmp",
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Self-test failed: staged indexed BMP preview did not render.");
            AssertEqual(16, staged.Width, "staged preview width");
            AssertPixel(staged, x: 1, y: 0, blue: 96, green: 104, red: 112, alpha: 255, "staged preview pixel");

            Assert(
                await workflow.LoadStagedTexturePreviewAsync(
                        paths,
                        previewManifestPath,
                        "zone.s3d",
                        "crystal.dds",
                        cancellationToken)
                    .ConfigureAwait(false) is null,
                "DDS staged member falls back to the toolchain preview path");
            Assert(
                await workflow.LoadStagedTexturePreviewAsync(
                        paths,
                        previewManifestPath,
                        "zone.s3d",
                        "missing.bmp",
                        cancellationToken)
                    .ConfigureAwait(false) is null,
                "missing member renders nothing");
            Assert(
                await workflow.LoadStagedTexturePreviewAsync(
                        paths,
                        previewManifestPath,
                        Path.Combine("..", "escape.s3d"),
                        "wall.bmp",
                        cancellationToken)
                    .ConfigureAwait(false) is null,
                "archive path cannot escape the staged payload root");
            Assert(
                await workflow.LoadOriginalTexturePreviewAsync(
                        paths,
                        previewManifestPath,
                        "zone.s3d",
                        "wall.bmp",
                        cancellationToken)
                    .ConfigureAwait(false) is null,
                "original preview without a build manifest resolves to nothing instead of failing");
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static byte[] CreateIndexedBmp(
        int width,
        int height,
        bool topDown,
        IReadOnlyList<(byte Blue, byte Green, byte Red)> palette,
        IReadOnlyList<byte[]> indexRowsTopFirst)
    {
        var stride = (width + 3) & ~3;
        var pixelOffset = 14 + 40 + 256 * 4;
        var bytes = new byte[pixelOffset + stride * height];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), pixelOffset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), topDown ? -height : height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), stride * height);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(46), 256);
        for (var index = 0; index < palette.Count; index++)
        {
            bytes[54 + index * 4] = palette[index].Blue;
            bytes[54 + index * 4 + 1] = palette[index].Green;
            bytes[54 + index * 4 + 2] = palette[index].Red;
        }

        for (var row = 0; row < height; row++)
        {
            var fileRow = topDown ? row : height - 1 - row;
            indexRowsTopFirst[row].CopyTo(bytes.AsSpan(pixelOffset + fileRow * stride, width));
        }

        return bytes;
    }

    private static void AssertPixel(
        DecodedTexturePreview decoded,
        int x,
        int y,
        byte blue,
        byte green,
        byte red,
        byte alpha,
        string description)
    {
        var offset = (y * decoded.Width + x) * 4;
        if (decoded.BgraPixels[offset] != blue
            || decoded.BgraPixels[offset + 1] != green
            || decoded.BgraPixels[offset + 2] != red
            || decoded.BgraPixels[offset + 3] != alpha)
        {
            throw new InvalidOperationException(
                $"Self-test failed: {description}; expected BGRA ({blue},{green},{red},{alpha}), got "
                + $"({decoded.BgraPixels[offset]},{decoded.BgraPixels[offset + 1]},{decoded.BgraPixels[offset + 2]},{decoded.BgraPixels[offset + 3]}).");
        }
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
}
