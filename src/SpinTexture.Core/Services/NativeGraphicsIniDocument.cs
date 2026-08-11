using System.Security.Cryptography;
using System.Text;

namespace SpinTexture.Core.Services;

internal readonly record struct NativeGraphicsIniValue(bool Exists, string? Value);

internal sealed class NativeGraphicsIniDocument
{
    private readonly List<string> lines;
    private readonly Encoding encoding;
    private readonly byte[] preamble;
    private readonly string newline;
    private readonly bool endsWithNewline;

    private NativeGraphicsIniDocument(
        List<string> lines,
        Encoding encoding,
        byte[] preamble,
        string newline,
        bool endsWithNewline)
    {
        this.lines = lines;
        this.encoding = encoding;
        this.preamble = preamble;
        this.newline = newline;
        this.endsWithNewline = endsWithNewline;
    }

    public static NativeGraphicsIniDocument Parse(ReadOnlySpan<byte> bytes)
    {
        var (encoding, preambleLength) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes[preambleLength..]);
        var newline = text.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : text.Contains('\n')
                ? "\n"
                : text.Contains('\r')
                    ? "\r"
                    : Environment.NewLine;
        var endsWithNewline = text.EndsWith("\r\n", StringComparison.Ordinal)
            || text.EndsWith('\n')
            || text.EndsWith('\r');
        var parsedLines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).ToList();
        if (endsWithNewline && parsedLines.Count != 0 && parsedLines[^1].Length == 0)
        {
            parsedLines.RemoveAt(parsedLines.Count - 1);
        }

        return new NativeGraphicsIniDocument(
            parsedLines,
            encoding,
            bytes[..preambleLength].ToArray(),
            newline,
            endsWithNewline);
    }

    public NativeGraphicsIniDocument Clone() => Parse(ToBytes());

    public NativeGraphicsIniValue Get(string section, string key)
    {
        var occurrences = FindKeyOccurrences(section, key, out _);
        if (occurrences.Count > 1)
        {
            throw new InvalidDataException($"eqclient.ini contains duplicate [{section}] {key} settings.");
        }

        if (occurrences.Count == 0)
        {
            return new NativeGraphicsIniValue(false, null);
        }

        var line = lines[occurrences[0]];
        var separator = line.IndexOf('=');
        return new NativeGraphicsIniValue(true, line[(separator + 1)..].Trim());
    }

    public void Set(string section, string key, bool exists, string? value)
    {
        var occurrences = FindKeyOccurrences(section, key, out var sectionHeaders);
        if (occurrences.Count > 1)
        {
            throw new InvalidDataException($"eqclient.ini contains duplicate [{section}] {key} settings.");
        }

        if (!exists)
        {
            if (occurrences.Count == 1)
            {
                lines.RemoveAt(occurrences[0]);
            }

            return;
        }

        ArgumentNullException.ThrowIfNull(value);
        if (occurrences.Count == 1)
        {
            var index = occurrences[0];
            var line = lines[index];
            var separator = line.IndexOf('=');
            var suffix = line[(separator + 1)..];
            var leadingWhitespace = suffix.Length - suffix.TrimStart().Length;
            var trailingWhitespace = suffix.Length - suffix.TrimEnd().Length;
            trailingWhitespace = Math.Min(trailingWhitespace, suffix.Length - leadingWhitespace);
            var leading = suffix[..leadingWhitespace];
            var trailing = trailingWhitespace == 0 ? string.Empty : suffix[^trailingWhitespace..];
            lines[index] = line[..(separator + 1)] + leading + value + trailing;
            return;
        }

        if (sectionHeaders.Count > 1)
        {
            throw new InvalidDataException(
                $"eqclient.ini repeats the [{section}] section, so adding {key} would be ambiguous.");
        }

        if (sectionHeaders.Count == 0)
        {
            if (lines.Count != 0 && lines[^1].Length != 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return;
        }

        var insertion = sectionHeaders[0] + 1;
        while (insertion < lines.Count && !TryReadSection(lines[insertion], out _))
        {
            insertion++;
        }

        lines.Insert(insertion, $"{key}={value}");
    }

    public byte[] ToBytes()
    {
        var text = string.Join(newline, lines) + (endsWithNewline ? newline : string.Empty);
        var payload = encoding.GetBytes(text);
        if (preamble.Length == 0)
        {
            return payload;
        }

        var result = new byte[preamble.Length + payload.Length];
        preamble.CopyTo(result, 0);
        payload.CopyTo(result, preamble.Length);
        return result;
    }

    public static (long Length, string Sha256) Fingerprint(ReadOnlySpan<byte> bytes) =>
        (bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));

    private List<int> FindKeyOccurrences(
        string requestedSection,
        string requestedKey,
        out List<int> sectionHeaders)
    {
        var occurrences = new List<int>();
        sectionHeaders = [];
        string? currentSection = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (TryReadSection(line, out var section))
            {
                currentSection = section;
                if (section.Equals(requestedSection, StringComparison.OrdinalIgnoreCase))
                {
                    sectionHeaders.Add(index);
                }

                continue;
            }

            if (!string.Equals(currentSection, requestedSection, StringComparison.OrdinalIgnoreCase)
                || !TryReadKey(line, out var key)
                || !key.Equals(requestedKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            occurrences.Add(index);
        }

        return occurrences;
    }

    private static bool TryReadSection(string line, out string section)
    {
        var trimmed = line.Trim();
        if (trimmed.Length >= 3 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            section = trimmed[1..^1].Trim();
            return section.Length != 0;
        }

        section = string.Empty;
        return false;
    }

    private static bool TryReadKey(string line, out string key)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is ';' or '#' or '[')
        {
            key = string.Empty;
            return false;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            key = string.Empty;
            return false;
        }

        key = line[..separator].Trim();
        return key.Length != 0;
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(false, true), 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (new UnicodeEncoding(false, false, true), 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (new UnicodeEncoding(true, false, true), 2);
        }

        try
        {
            var utf8 = new UTF8Encoding(false, true);
            _ = utf8.GetString(bytes);
            return (utf8, 0);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1, 0);
        }
    }
}
