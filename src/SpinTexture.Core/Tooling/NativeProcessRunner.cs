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

        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {command.DisplayName ?? executablePath}.");
        }

        var standardOutput = DrainAsync(
            process.StandardOutput,
            NativeOutputStream.StandardOutput,
            progress);
        var standardError = DrainAsync(
            process.StandardError,
            NativeOutputStream.StandardError,
            progress);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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
        IProgress<NativeOutputLine>? progress)
    {
        var retainedTail = new BoundedTextTail(MaximumRetainedOutputCharacters);

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            progress?.Report(new NativeOutputLine(outputStream, line));
            retainedTail.AppendLine(line);
        }

        return retainedTail.ToString();
    }

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
