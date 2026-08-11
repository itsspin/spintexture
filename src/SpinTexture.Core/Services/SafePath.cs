namespace SpinTexture.Core.Services;

public static class SafePath
{
    public static string RequireDescendant(string parentPath, string candidatePath, string parameterName)
    {
        string parent = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Path must remain under {parent}.", parameterName);
        }

        return candidate;
    }

    public static string RequireFileName(string fileName, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Expected a safe file name without directory components.", parameterName);
        }

        return fileName;
    }
}
