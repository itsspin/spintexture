using System.Globalization;
using System.Text.RegularExpressions;
using SpinTexture.Core.Pipeline;

namespace SpinTexture.Core.Services;

public enum LaunchPadFileAction
{
    Created,
    Patched,
    Replaced,
    Removed
}

public sealed record LaunchPadChangedFile(
    string RelativeInstallPath,
    LaunchPadFileAction Action,
    DateTimeOffset SessionStartedUtc);

public sealed record LaunchPadUpdateEvidence(
    bool IsCompleted,
    bool HasUnsafePath,
    DateTimeOffset? LatestSessionStartedUtc,
    IReadOnlyDictionary<string, LaunchPadChangedFile> ChangedFiles,
    string Summary)
{
    public bool HasRelevantChanges => IsCompleted && ChangedFiles.Count > 0;

    public bool TryGetChangedFile(
        string relativeInstallPath,
        out LaunchPadChangedFile? changedFile)
    {
        changedFile = null;
        if (string.IsNullOrWhiteSpace(relativeInstallPath))
        {
            return false;
        }

        return ChangedFiles.TryGetValue(
            NormalizeLookupPath(relativeInstallPath),
            out changedFile);
    }

    private static string NormalizeLookupPath(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
}

/// <summary>
/// Reads LaunchPad's append-only download log and identifies files explicitly
/// changed by completed update sessions after a SpinTexture install. This is
/// evidence only: callers still exact-hash the live files and enforce their own
/// transactional race gates before adopting updated originals.
/// </summary>
public sealed partial class LaunchPadUpdateEvidenceService
{
    public const string DownloadLogFileName = ".DownloadInfo.txt";

    public async Task<LaunchPadUpdateEvidence> InspectAsync(
        ProjectPaths paths,
        DateTimeOffset activeInstallAppliedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        var installRoot = Path.GetFullPath(paths.InstallPath);
        var logPath = PathGuard.ResolveUnderRoot(installRoot, DownloadLogFileName);
        if (!File.Exists(logPath))
        {
            return NotCompleted(
                "LaunchPad's update log was not found. Run the official launcher and let its update finish before refreshing the texture pack.");
        }

        string[] lines;
        try
        {
            await using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            var collected = new List<string>();
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                collected.Add(line);
            }

            lines = collected.ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return NotCompleted(
                $"SpinTexture could not read LaunchPad's update log safely: {exception.Message}");
        }

        var sessions = ParseSessions(lines);
        var firstPostInstallIndex = sessions.FindIndex(session =>
            session.StartedUtc is { } startedUtc
            && startedUtc >= activeInstallAppliedUtc);
        if (firstPostInstallIndex < 0)
        {
            return NotCompleted(
                "No LaunchPad update session completed after the active SpinTexture pack was installed.");
        }

        var lastKnownPreInstallIndex = -1;
        for (var index = 0; index < firstPostInstallIndex; index++)
        {
            if (sessions[index].StartedUtc is { } startedUtc
                && startedUtc < activeInstallAppliedUtc)
            {
                lastKnownPreInstallIndex = index;
            }
        }

        if (sessions
            .Skip(lastKnownPreInstallIndex + 1)
            .Take(firstPostInstallIndex - lastKnownPreInstallIndex - 1)
            .Any(session => session.StartedUtc is null))
        {
            return new LaunchPadUpdateEvidence(
                IsCompleted: false,
                HasUnsafePath: false,
                LatestSessionStartedUtc: null,
                EmptyChanges(),
                "LaunchPad's latest update log contains a session timestamp SpinTexture cannot validate. No updated originals were trusted.");
        }

        // Once one post-install session anchors the append-only suffix, every
        // later header remains post-install even if the Windows clock moves
        // backward across AppliedUtc. Filtering each later session by its wall
        // clock could otherwise ignore an incomplete overwrite and trust stale
        // evidence for the bytes it replaced.
        var relevantSessions = sessions
            .Skip(firstPostInstallIndex)
            .ToArray();
        if (relevantSessions.Any(session => session.StartedUtc is null))
        {
            return new LaunchPadUpdateEvidence(
                IsCompleted: false,
                HasUnsafePath: false,
                LatestSessionStartedUtc: null,
                EmptyChanges(),
                "LaunchPad's latest update log contains a session timestamp SpinTexture cannot validate. No updated originals were trusted.");
        }

        var latest = relevantSessions[^1];
        if (!latest.IsCompleted)
        {
            return new LaunchPadUpdateEvidence(
                IsCompleted: false,
                HasUnsafePath: false,
                latest.StartedUtc,
                EmptyChanges(),
                "The latest LaunchPad session did not record a successful completion. Reopen the official launcher and let verification finish before refreshing the texture pack.");
        }

        var changed = new Dictionary<string, LaunchPadChangedFile>(
            StringComparer.OrdinalIgnoreCase);
        // A later "All files are up to date" session is a global verification
        // of the current client. It can therefore complete trust for explicit
        // paths written by an earlier interrupted session. Without that later
        // verification, an interrupted session invalidates all earlier path
        // evidence: it may have overwritten a previously authorized file
        // before a later, unrelated completed session was appended. Later
        // individually completed sessions can establish fresh evidence again.
        var globallyVerified = latest.Completion == SessionCompletion.AllFilesUpToDate;
        foreach (var session in relevantSessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!globallyVerified && !session.IsCompleted)
            {
                changed.Clear();
                continue;
            }

            foreach (var action in session.Actions)
            {
                if (!TryNormalizeChangedPath(
                        installRoot,
                        action.Path,
                        out var relativePath))
                {
                    return new LaunchPadUpdateEvidence(
                        IsCompleted: false,
                        HasUnsafePath: true,
                        latest.StartedUtc,
                        EmptyChanges(),
                        "LaunchPad's completed update log contains an unsafe or invalid file path. SpinTexture will not adopt any updated originals from that log.");
                }

                changed[relativePath] = new LaunchPadChangedFile(
                    relativePath,
                    action.Action,
                    session.StartedUtc!.Value);
            }
        }

        return new LaunchPadUpdateEvidence(
            IsCompleted: true,
            HasUnsafePath: false,
            latest.StartedUtc,
            changed,
            changed.Count == 0
                ? "LaunchPad completed after the active pack was installed and reported no changed files."
                : $"LaunchPad completed after the active pack was installed and explicitly recorded {changed.Count:N0} changed file(s).");
    }

    private static List<ParsedSession> ParseSessions(IReadOnlyList<string> lines)
    {
        var sessions = new List<ParsedSession>();
        ParsedSessionBuilder? current = null;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var header = SessionHeaderRegex().Match(line);
            if (header.Success)
            {
                if (current is not null)
                {
                    sessions.Add(current.Build());
                }

                current = new ParsedSessionBuilder(
                    TryParseSessionTimestamp(
                        header.Groups["timestamp"].Value,
                        out var startedUtc)
                        ? startedUtc
                        : null,
                    index);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line.StartsWith("Finished downloading ", StringComparison.OrdinalIgnoreCase))
            {
                current.Completion = SessionCompletion.FinishedDownloading;
                continue;
            }

            if (line.EndsWith(":All files are up to date", StringComparison.OrdinalIgnoreCase))
            {
                current.Completion = SessionCompletion.AllFilesUpToDate;
                continue;
            }

            var found = FoundFilesRegex().Match(line);
            if (found.Success
                && int.TryParse(
                    found.Groups["count"].Value,
                    NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture,
                    out var expectedFiles))
            {
                if (current.Completion != SessionCompletion.None)
                {
                    current.HasStructuredRecordAfterCompletion = true;
                }

                current.ExpectedUpdatedFileCount = expectedFiles;
                continue;
            }

            var action = FileActionRegex().Match(line);
            if (!action.Success)
            {
                continue;
            }

            if (current.Completion != SessionCompletion.None)
            {
                current.HasStructuredRecordAfterCompletion = true;
            }

            current.Actions.Add(new ParsedAction(
                ParseAction(action.Groups["verb"].Value),
                action.Groups["path"].Value.Trim()));
        }

        if (current is not null)
        {
            sessions.Add(current.Build());
        }

        return sessions;
    }

    private static bool TryParseSessionTimestamp(
        string text,
        out DateTimeOffset startedUtc)
    {
        var match = SessionTimestampRegex().Match(text);
        if (match.Success
            && DateTime.TryParseExact(
                $"{match.Groups["month"].Value} "
                + $"{match.Groups["day"].Value} "
                + $"{match.Groups["time"].Value} "
                + match.Groups["year"].Value,
                "MMM d HH:mm:ss yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            startedUtc = new DateTimeOffset(parsed).ToUniversalTime();
            return true;
        }

        startedUtc = default;
        return false;
    }

    private static LaunchPadFileAction ParseAction(string verb) => verb switch
    {
        var value when value.Equals("Creating", StringComparison.OrdinalIgnoreCase) =>
            LaunchPadFileAction.Created,
        var value when value.Equals("Patching", StringComparison.OrdinalIgnoreCase) =>
            LaunchPadFileAction.Patched,
        var value when value.Equals("Replacing", StringComparison.OrdinalIgnoreCase) =>
            LaunchPadFileAction.Replaced,
        var value when value.Equals("Removing", StringComparison.OrdinalIgnoreCase) =>
            LaunchPadFileAction.Removed,
        _ => throw new InvalidDataException($"Unknown LaunchPad file action: {verb}")
    };

    private static bool TryNormalizeChangedPath(
        string installRoot,
        string loggedPath,
        out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(loggedPath))
        {
            return false;
        }

        var candidate = loggedPath.Trim();
        var startsQuoted = candidate.StartsWith('"');
        var endsQuoted = candidate.EndsWith('"');
        if (startsQuoted != endsQuoted)
        {
            return false;
        }

        if (startsQuoted)
        {
            candidate = candidate[1..^1];
        }

        var pathSegments = candidate.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.None);
        if (pathSegments.Any(segment =>
                segment.Length == 0
                || segment.Equals(".", StringComparison.Ordinal)
                || segment.Equals("..", StringComparison.Ordinal)
                || segment.Contains(':', StringComparison.Ordinal)))
        {
            return false;
        }

        candidate = candidate.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(candidate)
            || Path.IsPathRooted(candidate))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(installRoot, candidate));
            if (!PathGuard.IsPathUnderRoot(installRoot, fullPath))
            {
                return false;
            }

            relativePath = Path.GetRelativePath(installRoot, fullPath)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return relativePath.Length > 0
                && !relativePath.Equals(".", StringComparison.Ordinal)
                && !relativePath.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                && !Path.IsPathRooted(relativePath);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return false;
        }
    }

    private static LaunchPadUpdateEvidence NotCompleted(string summary) => new(
        IsCompleted: false,
        HasUnsafePath: false,
        LatestSessionStartedUtc: null,
        EmptyChanges(),
        summary);

    private static IReadOnlyDictionary<string, LaunchPadChangedFile> EmptyChanges() =>
        new Dictionary<string, LaunchPadChangedFile>(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(
        @"^\*{4}\s+Starting at\s+(?<timestamp>.+?)\s+with plug-in\s+.+?\s+\*{4}\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SessionHeaderRegex();

    [GeneratedRegex(
        @"^[A-Za-z]{3}\s+(?<month>[A-Za-z]{3})\s+(?<day>\d{1,2})\s+(?<time>\d{1,2}:\d{2}:\d{2})\s+(?<year>\d{4})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SessionTimestampRegex();

    [GeneratedRegex(
        @"^[0-9A-Fa-f]+-\d+:\d{2}:\d{2}:(?<verb>Creating|Patching|Replacing|Removing)\s+(?<path>.+?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FileActionRegex();

    [GeneratedRegex(
        @"^[0-9A-Fa-f]+-\d+:\d{2}:\d{2}:Found\s+(?<count>[\d,]+)\s+file\(s\)\s+to update\.\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FoundFilesRegex();

    private sealed record ParsedAction(LaunchPadFileAction Action, string Path);

    private sealed record ParsedSession(
        DateTimeOffset? StartedUtc,
        int StartLine,
        SessionCompletion Completion,
        int? ExpectedUpdatedFileCount,
        IReadOnlyList<ParsedAction> Actions)
    {
        public bool IsCompleted => Completion != SessionCompletion.None;
    }

    private enum SessionCompletion
    {
        None,
        FinishedDownloading,
        AllFilesUpToDate
    }

    private sealed class ParsedSessionBuilder(
        DateTimeOffset? startedUtc,
        int startLine)
    {
        public DateTimeOffset? StartedUtc { get; } = startedUtc;
        public int StartLine { get; } = startLine;
        public SessionCompletion Completion { get; set; }
        public int? ExpectedUpdatedFileCount { get; set; }
        public List<ParsedAction> Actions { get; } = [];
        public bool HasStructuredRecordAfterCompletion { get; set; }

        public ParsedSession Build()
        {
            var completion = Completion;
            if (HasStructuredRecordAfterCompletion
                || (completion == SessionCompletion.FinishedDownloading
                && (ExpectedUpdatedFileCount is not { } expected
                    || Actions
                        .Where(action => action.Action != LaunchPadFileAction.Removed)
                        .Select(action => action.Path)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count() != expected)))
            {
                completion = SessionCompletion.None;
            }

            return new ParsedSession(
                StartedUtc,
                StartLine,
                completion,
                ExpectedUpdatedFileCount,
                Actions.ToArray());
        }
    }
}
