namespace SpinTexture.Core.Pipeline;

internal static class PathGuard
{
    public static string ResolveUnderRoot(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathFullyQualified(relativePath)
            || relativePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new InvalidDataException($"Manifest path must be relative: {relativePath}");
        }

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        EnsureUnderRoot(fullRoot, fullPath);
        EnsureNoReparsePointTraversal(fullRoot, fullPath);
        return fullPath;
    }

    public static string EnsurePathUnderRoot(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(path);
        EnsureUnderRoot(fullRoot, fullPath);
        EnsureNoReparsePointTraversal(fullRoot, fullPath);
        return fullPath;
    }

    public static string ValidateIdentifier(string? requested, string prefix)
    {
        var identifier = string.IsNullOrWhiteSpace(requested)
            ? $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"
            : requested;

        if (identifier.Length > 100
            || identifier is "." or ".."
            || identifier.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Transaction identifier contains invalid filename characters.", nameof(requested));
        }

        return identifier;
    }

    public static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    public static bool IsPathUnderRoot(string root, string path)
    {
        try
        {
            EnsurePathUnderRoot(root, path);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void EnsureUnderRoot(string fullRoot, string fullPath)
    {
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative == "."
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathFullyQualified(relative))
        {
            throw new InvalidDataException($"Path escapes or resolves to the transaction root: {fullPath}");
        }
    }

    private static void EnsureNoReparsePointTraversal(string fullRoot, string fullPath)
    {
        if (Directory.Exists(fullRoot)
            && (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Transaction root cannot be a reparse point: {fullRoot}");
        }

        var relative = Path.GetRelativePath(fullRoot, fullPath);
        var current = fullRoot;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Transaction path traverses a reparse point: {current}");
            }
        }
    }
}
