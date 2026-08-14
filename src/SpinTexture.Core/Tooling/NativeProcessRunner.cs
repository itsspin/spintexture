using System.Diagnostics;

namespace SpinTexture.Core.Tooling;

public sealed class NativeProcessRunner : INativeProcessRunner
{
    internal const int MaximumRetainedOutputCharacters = 64 * 1024;

    public async Task<NativeProcessResult> RunAsync(
        NativeProcessCommand command,
        IProgress<NativeOutputLine>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ExecutablePath);

        var executablePath = Path.GetFullPath(command.ExecutablePath);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Native tool executable was not found.", executablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = ResolveWorkingDirectory(command.WorkingDirectory, executablePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (command.Environment is not null)
        {
            foreach (var pair in command.Environment)
            {
                if (pair.Value is null)
                {
                    startInfo.Environment.Remove(pair.Key);
                }
                else
                {
                    startInfo.Environment[pair.Key] = pair.Value;
                }
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();
        long lastActivityTimestamp = 0;

        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {command.DisplayName ?? executablePath}.");
        }

        Interlocked.Exchange(ref lastActivityTimestamp, Stopwatch.GetTimestamp());

        var standardOutput = DrainAsync(
            process.StandardOutput,
            NativeOutputStream.StandardOutput,
            progress,
            () => Interlocked.Exchange(ref lastActivityTimestamp, Stopwatch.GetTimestamp()));
        var standardError = DrainAsync(
            process.StandardError,
            NativeOutputStream.StandardError,
            progress,
            () => Interlocked.Exchange(ref lastActivityTimestamp, Stopwatch.GetTimestamp()));

        try
        {
            await WaitForExitWithInactivityGuardAsync(
                process,
                command,
                () => Interlocked.Read(ref lastActivityTimestamp),
                cancellationToken).ConfigureAwait(false);
        }
        catch (NativeProcessInactivityException)
        {
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            throw;
        }

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        stopwatch.Stop();

        return new NativeProcessResult(process.ExitCode, output, error, stopwatch.Elapsed);
    }

    private static async Task<string> DrainAsync(
        StreamReader reader,
        NativeOutputStream outputStream,
        IProgress<NativeOutputLine>? progress,
        Action markActivity)
    {
        var retainedTail = new BoundedTextTail(MaximumRetainedOutputCharacters);

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            markActivity();
            progress?.Report(new NativeOutputLine(outputStream, line));
            retainedTail.AppendLine(line);
        }

        return retainedTail.ToString();
    }

    private static async Task WaitForExitWithInactivityGuardAsync(
        Process process,
        NativeProcessCommand command,
        Func<long> readLastActivityTimestamp,
        CancellationToken cancellationToken)
    {
        if (command.InactivityTimeout is null)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var inactivityTimeout = command.InactivityTimeout.Value;
        if (inactivityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "A native-process inactivity timeout must be positive.");
        }

        var pollInterval = TimeSpan.FromSeconds(Math.Clamp(
            inactivityTimeout.TotalSeconds / 12,
            1,
            15));
        var exitTask = process.WaitForExitAsync(cancellationToken);
        while (!exitTask.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastActivity = readLastActivityTimestamp();
            var quietFor = Stopwatch.GetElapsedTime(lastActivity);
            if (quietFor >= inactivityTimeout)
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw new NativeProcessInactivityException(
                    $"{command.DisplayName ?? command.ExecutablePath} stopped responding for "
                    + $"{FormatInactivityTimeout(inactivityTimeout)}. The worker was stopped so this batch can be retried safely.",
                    command,
                    quietFor);
            }

            var remaining = inactivityTimeout - quietFor;
            var delayTask = Task.Delay(
                remaining < pollInterval ? remaining : pollInterval,
                cancellationToken);
            if (await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false) == exitTask)
            {
                await exitTask.ConfigureAwait(false);
                return;
            }
        }

        await exitTask.ConfigureAwait(false);
    }

    private static string FormatInactivityTimeout(TimeSpan timeout) =>
        timeout < TimeSpan.FromMinutes(1)
            ? $"{timeout.TotalSeconds:0.##} seconds"
            : $"{timeout.TotalMinutes:0.##} minutes";

    private static string ResolveWorkingDirectory(string? requested, string executablePath)
    {
        var directory = string.IsNullOrWhiteSpace(requested)
            ? Path.GetDirectoryName(executablePath)
            : Path.GetFullPath(requested);

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Native tool working directory was not found: {directory}");
        }

        return directory;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and Kill.
        }
    }
}

public sealed class NativeProcessInactivityException : Exception
{
    public NativeProcessInactivityException(
        string message,
        NativeProcessCommand command,
        TimeSpan inactiveDuration)
        : base(message)
    {
        Command = command;
        InactiveDuration = inactiveDuration;
    }

    public NativeProcessCommand Command { get; }
    public TimeSpan InactiveDuration { get; }
}

internal sealed class BoundedTextTail
{
    private readonly char[] buffer;
    private int start;
    private int count;

    public BoundedTextTail(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        buffer = new char[capacity];
    }

    public void AppendLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (count != 0)
        {
            Append(Environment.NewLine.AsSpan());
        }

        Append(line.AsSpan());
    }

    public override string ToString()
    {
        if (count == 0)
        {
            return string.Empty;
        }

        var result = new char[count];
        var firstLength = Math.Min(count, buffer.Length - start);
        buffer.AsSpan(start, firstLength).CopyTo(result);
        if (firstLength < count)
        {
            buffer.AsSpan(0, count - firstLength).CopyTo(result.AsSpan(firstLength));
        }

        return new string(result);
    }

    private void Append(ReadOnlySpan<char> value)
    {
        if (value.Length >= buffer.Length)
        {
            value[^buffer.Length..].CopyTo(buffer);
            start = 0;
            count = buffer.Length;
            return;
        }

        var overflow = Math.Max(0, count + value.Length - buffer.Length);
        if (overflow != 0)
        {
            start = (start + overflow) % buffer.Length;
            count -= overflow;
        }

        var end = (start + count) % buffer.Length;
        var firstLength = Math.Min(value.Length, buffer.Length - end);
        value[..firstLength].CopyTo(buffer.AsSpan(end));
        if (firstLength < value.Length)
        {
            value[firstLength..].CopyTo(buffer);
        }

        count += value.Length;
    }
}
