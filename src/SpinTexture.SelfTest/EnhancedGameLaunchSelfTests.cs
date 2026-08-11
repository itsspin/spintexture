using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class EnhancedGameLaunchSelfTests
{
    public static void Run()
    {
        const string installArgument = @"C:\Games\EverQuest Legends";
        Assert(
            EnhancedGameLaunch.TryParseArguments(
                [EnhancedGameLaunch.CommandArgument, installArgument],
                out var parsedInstall),
            "the exact enhanced-play command should parse");
        AssertEqual(installArgument, parsedInstall, "parsed enhanced-play install path");

        AssertRejected([], "empty command line");
        AssertRejected([EnhancedGameLaunch.CommandArgument], "missing install path");
        AssertRejected(
            [EnhancedGameLaunch.CommandArgument, string.Empty],
            "empty install path");
        AssertRejected(
            [EnhancedGameLaunch.CommandArgument, "   "],
            "blank install path");
        AssertRejected(
            ["--PLAY-ENHANCED", installArgument],
            "non-exact command spelling");
        AssertRejected(
            [EnhancedGameLaunch.CommandArgument, installArgument, "extra"],
            "extra command argument");
        AssertRejected(
            [EnhancedGameLaunch.CommandArgument, installArgument, "/ticket:secret"],
            "appended login ticket");
        AssertRejected(
            [EnhancedGameLaunch.CommandArgument, "/ticket"],
            "ticket in place of install path");
        AssertRejected(
            [EnhancedGameLaunch.CommandArgument, "/ticket:secret"],
            "credential ticket in place of install path");
        AssertRejected(
            [EnhancedGameLaunch.CommandArgument, " /TICKET=secret"],
            "case-insensitive credential ticket");

        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-enhanced-launch-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest Legends");
        Directory.CreateDirectory(installPath);

        try
        {
            AssertThrows<FileNotFoundException>(
                () => EnhancedGameLaunch.CreateStartInfo(installPath),
                "install without eqgame.exe");

            var executablePath = Path.Combine(installPath, "eqgame.exe");
            File.WriteAllBytes(executablePath, "test executable placeholder"u8.ToArray());
            var startInfo = EnhancedGameLaunch.CreateStartInfo(
                installPath + Path.DirectorySeparatorChar);

            AssertEqual(
                Path.GetFullPath(executablePath),
                Path.GetFullPath(startInfo.FileName),
                "enhanced launch executable");
            AssertEqual(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath)),
                startInfo.WorkingDirectory,
                "enhanced launch working directory");
            Assert(startInfo.UseShellExecute, "enhanced launch should use the Windows shell");
            AssertEqual(1, startInfo.ArgumentList.Count, "enhanced launch argument count");
            AssertEqual("patchme", startInfo.ArgumentList[0], "enhanced launch argument");
            Assert(
                string.IsNullOrEmpty(startInfo.Arguments),
                "enhanced launch should not use an unstructured argument string");
            Assert(
                startInfo.ArgumentList.All(argument =>
                    !argument.Contains("ticket", StringComparison.OrdinalIgnoreCase)),
                "enhanced launch must never forward a login ticket");
            Assert(
                !startInfo.ArgumentList.Contains(installArgument),
                "enhanced launch must not forward the install path as a game argument");

            AssertThrows<ArgumentException>(
                () => EnhancedGameLaunch.CreateStartInfo("   "),
                "blank launch install path");
            AssertThrows<DirectoryNotFoundException>(
                () => EnhancedGameLaunch.CreateStartInfo(Path.Combine(root, "missing")),
                "missing launch install directory");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static void AssertRejected(string[] arguments, string description)
    {
        Assert(
            !EnhancedGameLaunch.TryParseArguments(arguments, out var installPath),
            $"{description} should be rejected");
        AssertEqual(string.Empty, installPath, $"{description} rejected result");
    }

    private static void AssertThrows<TException>(Action action, string description)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Self-test failed: {description} did not throw {typeof(TException).Name}.");
    }

    private static void DeleteTree(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(root, recursive: true);
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
