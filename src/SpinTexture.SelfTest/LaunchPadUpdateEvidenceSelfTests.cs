using SpinTexture.Core;
using SpinTexture.Core.Services;

namespace SpinTexture.SelfTest;

internal static class LaunchPadUpdateEvidenceSelfTests
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"spintexture-launchpad-evidence-{Guid.NewGuid():N}");
        var installPath = Path.Combine(root, "EverQuest");
        var workspacePath = Path.Combine(root, "Workspace");
        Directory.CreateDirectory(installPath);

        try
        {
            var paths = new ProjectPaths(installPath, workspacePath);
            var appliedUtc = new DateTimeOffset(2026, 8, 9, 18, 0, 0, TimeSpan.Zero);
            var oldSession = appliedUtc.AddHours(-1);
            var patchSession = appliedUtc.AddMinutes(15);
            var verificationSession = appliedUtc.AddMinutes(25);
            await WriteLogAsync(
                installPath,
                [
                    Header(oldSession),
                    "1111-00:00:00:Found 1 file(s) to update.",
                    "1111-00:00:01:Creating ignored-old.s3d",
                    "Finished downloading 100 bytes in 0.1 seconds (1,000 bytes per second)",
                    string.Empty,
                    Header(patchSession),
                    "2222-00:00:00:Found 3 file(s) to update.",
                    "2222-00:00:01:Creating zones/new zone.s3d",
                    "2222-00:00:02:Patching soldungb_obj.s3d",
                    "2222-00:00:03:Replacing Textures/interface.eqg",
                    "Finished downloading 300 bytes in 0.2 seconds (1,500 bytes per second)",
                    string.Empty,
                    Header(verificationSession),
                    "3333-00:00:00:Checking game installation...",
                    "3333-00:00:01:All files are up to date"
                ],
                cancellationToken).ConfigureAwait(false);

            var service = new LaunchPadUpdateEvidenceService();
            var evidence = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            Assert(
                evidence.IsCompleted,
                $"patch plus later all-up-to-date is complete ({evidence.Summary})");
            Assert(!evidence.HasUnsafePath, "ordinary LaunchPad paths are safe");
            Assert(
                evidence.HasPostInstallActivity,
                "completed post-install session is marked for exact health detection");
            AssertEqual(3, evidence.ChangedFiles.Count, "completed post-install change count");
            Assert(
                !evidence.TryGetChangedFile("ignored-old.s3d", out _),
                "pre-install session is ignored");
            AssertChanged(
                evidence,
                "soldungb_obj.s3d",
                LaunchPadFileAction.Patched);
            AssertChanged(
                evidence,
                Path.Combine("zones", "new zone.s3d"),
                LaunchPadFileAction.Created);
            AssertChanged(
                evidence,
                Path.Combine("Textures", "interface.eqg"),
                LaunchPadFileAction.Replaced);

            await WriteLogAsync(
                installPath,
                [
                    Header(patchSession),
                    "2222-00:00:00:Found 1 file(s) to update.",
                    "2222-00:00:01:Patching soldungb_obj.s3d"
                ],
                cancellationToken).ConfigureAwait(false);
            var incomplete = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            Assert(!incomplete.IsCompleted, "incomplete latest session fails closed");
            Assert(
                incomplete.HasPostInstallActivity,
                "incomplete post-install session still forces exact health detection");
            AssertEqual(0, incomplete.ChangedFiles.Count, "incomplete actions are never trusted");

            await WriteLogAsync(
                installPath,
                [
                    Header(patchSession),
                    "2222-00:00:00:Found 2 file(s) to update.",
                    "2222-00:00:01:Patching soldungb_obj.s3d",
                    "Finished downloading 100 bytes in 0.1 seconds (1,000 bytes per second)"
                ],
                cancellationToken).ConfigureAwait(false);
            var countMismatch = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            Assert(!countMismatch.IsCompleted, "finished session with a truncated action list fails closed");

            await WriteLogAsync(
                installPath,
                [
                    Header(patchSession),
                    "2222-00:00:01:Patching soldungb_obj.s3d",
                    "Finished downloading 100 bytes in 0.1 seconds (1,000 bytes per second)"
                ],
                cancellationToken).ConfigureAwait(false);
            var missingFoundCount = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            Assert(!missingFoundCount.IsCompleted, "finished session without Found N metadata fails closed");

            await WriteLogAsync(
                installPath,
                [
                    Header(patchSession.AddMinutes(10)),
                    "2222-00:00:00:Found 1 file(s) to update.",
                    "2222-00:00:01:Patching soldungb_obj.s3d",
                    "Finished downloading 100 bytes in 0.1 seconds (1,000 bytes per second)",
                    // The Windows clock moved backward, but this session was
                    // appended later and is therefore authoritative.
                    Header(patchSession.AddMinutes(5)),
                    "3333-00:00:00:Found 1 file(s) to update.",
                    "3333-00:00:01:Patching second.s3d"
                ],
                cancellationToken).ConfigureAwait(false);
            var clockRollback = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            Assert(!clockRollback.IsCompleted, "append order beats a clock rollback for latest-session safety");

            await WriteLogAsync(
                installPath,
                [
                    Header(patchSession),
                    "2222-00:00:00:Found 1 file(s) to update.",
                    "2222-00:00:01:Patching soldungb_obj.s3d",
                    "Finished downloading 100 bytes in 0.1 seconds (1,000 bytes per second)",
                    // This later append rolled behind the install timestamp and
                    // rewrote the same path without a completion marker.
                    Header(appliedUtc.AddMinutes(-5)),
                    "3333-00:00:00:Found 1 file(s) to update.",
                    "3333-00:00:01:Replacing soldungb_obj.s3d"
                ],
                cancellationToken).ConfigureAwait(false);
            var crossBoundaryClockRollback = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            Assert(
                !crossBoundaryClockRollback.IsCompleted,
                "later appended session remains relevant after clock rolls behind AppliedUtc");

            await WriteLogAsync(
                installPath,
                [
                    Header(patchSession),
                    "2222-00:00:00:Found 1 file(s) to update.",
                    "2222-00:00:01:Patching soldungb_obj.s3d",
                    "Finished downloading 100 bytes in 0.1 seconds (1,000 bytes per second)",
                    "2222-00:00:02:Replacing soldungb_obj.s3d"
                ],
                cancellationToken).ConfigureAwait(false);
            var actionAfterCompletion = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            Assert(
                !actionAfterCompletion.IsCompleted,
                "structured actions after a completion marker fail the session closed");

            await WriteLogAsync(
                installPath,
                [
                    Header(patchSession),
                    "2222-00:00:00:Found 1 file(s) to update.",
                    "2222-00:00:01:Patching soldungb_obj.s3d",
                    "Finished downloading 100 bytes in 0.1 seconds (1,000 bytes per second)",
                    Header(patchSession.AddMinutes(1)),
                    "3333-00:00:00:Found 1 file(s) to update.",
                    "3333-00:00:01:Replacing soldungb_obj.s3d",
                    Header(patchSession.AddMinutes(2)),
                    "4444-00:00:00:Found 1 file(s) to update.",
                    "4444-00:00:01:Patching unrelated.s3d",
                    "Finished downloading 100 bytes in 0.1 seconds (1,000 bytes per second)"
                ],
                cancellationToken).ConfigureAwait(false);
            var interruptedOverwrite = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            Assert(interruptedOverwrite.IsCompleted, "later unrelated completed session is recognized");
            Assert(
                !interruptedOverwrite.TryGetChangedFile("soldungb_obj.s3d", out _),
                "interrupted overwrite invalidates stale completed evidence for the same path");
            AssertChanged(
                interruptedOverwrite,
                "unrelated.s3d",
                LaunchPadFileAction.Patched);

            await WriteLogAsync(
                installPath,
                [
                    Header(patchSession),
                    "2222-00:00:00:Found 1 file(s) to update.",
                    "2222-00:00:01:Patching soldungb_obj.s3d",
                    "2222-00:00:02:Replacing soldungb_obj.s3d",
                    "Finished downloading 100 bytes in 0.1 seconds (1,000 bytes per second)"
                ],
                cancellationToken).ConfigureAwait(false);
            var duplicateAction = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            AssertChanged(
                duplicateAction,
                "soldungb_obj.s3d",
                LaunchPadFileAction.Replaced);

            await AssertUnsafePathRejectedAsync(
                service,
                paths,
                appliedUtc,
                patchSession,
                "../outside.s3d",
                cancellationToken).ConfigureAwait(false);
            await AssertUnsafePathRejectedAsync(
                service,
                paths,
                appliedUtc,
                patchSession,
                @"C:\outside.s3d",
                cancellationToken).ConfigureAwait(false);
            await AssertUnsafePathRejectedAsync(
                service,
                paths,
                appliedUtc,
                patchSession,
                "zones/../soldungb_obj.s3d",
                cancellationToken).ConfigureAwait(false);
            await AssertUnsafePathRejectedAsync(
                service,
                paths,
                appliedUtc,
                patchSession,
                "soldungb_obj.s3d:alternate",
                cancellationToken).ConfigureAwait(false);
            await AssertUnsafePathRejectedAsync(
                service,
                paths,
                appliedUtc,
                patchSession,
                "\"soldungb_obj.s3d",
                cancellationToken).ConfigureAwait(false);

            await WriteLogAsync(
                installPath,
                [
                    Header(patchSession),
                    "2222-00:00:00:Found 1 file(s) to update.",
                    "2222-00:00:01:Patching soldungb_obj.s3d",
                    string.Empty,
                    Header(verificationSession),
                    "3333-00:00:00:Checking game installation...",
                    "3333-00:00:01:All files are up to date"
                ],
                cancellationToken).ConfigureAwait(false);
            var globallyVerifiedInterrupted = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            AssertChanged(
                globallyVerifiedInterrupted,
                "soldungb_obj.s3d",
                LaunchPadFileAction.Patched);

            await WriteLogAsync(
                installPath,
                [
                    "**** Starting at definitely-not-a-date with plug-in 1.0.3.204 ****",
                    Header(verificationSession),
                    "3333-00:00:01:All files are up to date"
                ],
                cancellationToken).ConfigureAwait(false);
            var malformedTimestamp = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            Assert(!malformedTimestamp.IsCompleted, "malformed later timestamp fails closed");
            Assert(
                malformedTimestamp.HasPostInstallActivity,
                "ambiguous timestamp before a known post-install session fails detection closed");

            await WriteLogAsync(
                installPath,
                [
                    Header(patchSession),
                    "2222-00:00:00:Found 0 file(s) to update.",
                    "2222-00:00:01:Removing obsolete.s3d",
                    "Finished downloading 0 bytes in 0.1 seconds (0 bytes per second)"
                ],
                cancellationToken).ConfigureAwait(false);
            var removal = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            AssertChanged(removal, "obsolete.s3d", LaunchPadFileAction.Removed);

            var realShapeSession = appliedUtc.AddDays(10);
            var realShapeLines = new List<string>
            {
                Header(realShapeSession),
                "4444-00:00:00:Found 59 file(s) to update."
            };
            realShapeLines.AddRange(Enumerable.Range(0, 59).Select(index =>
                $"4444-00:00:{index + 1:00}:Replacing patch-{index:00}.s3d"));
            realShapeLines.AddRange(Enumerable.Range(0, 8).Select(index =>
                $"4444-00:01:{index:00}:Removing docs/removed-{index:00}.pdf"));
            realShapeLines.Add(
                "Finished downloading 4,520,118 bytes in 2.0 seconds (2,260,059 bytes per second)");
            await WriteLogAsync(
                installPath,
                realShapeLines,
                cancellationToken).ConfigureAwait(false);
            var realShape = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            Assert(realShape.IsCompleted, "current 59-update plus 8-removal LaunchPad grammar is accepted");
            AssertEqual(67, realShape.ChangedFiles.Count, "current real-shaped action count");

            var paddedDaySession = new DateTimeOffset(
                2026,
                8,
                4,
                18,
                0,
                0,
                TimeSpan.Zero);
            var paddedHeader = Header(paddedDaySession)
                .Replace("Aug 4", "Aug  4", StringComparison.Ordinal);
            await WriteLogAsync(
                installPath,
                [
                    paddedHeader,
                    "5555-00:00:00:All files are up to date"
                ],
                cancellationToken).ConfigureAwait(false);
            var paddedDay = await service
                .InspectAsync(
                    paths,
                    paddedDaySession.AddMinutes(-1),
                    cancellationToken)
                .ConfigureAwait(false);
            Assert(paddedDay.IsCompleted, "single-digit space-padded LaunchPad date parses");

            await WriteLogAsync(
                installPath,
                [
                    Header(oldSession),
                    "6666-00:00:01:All files are up to date"
                ],
                cancellationToken).ConfigureAwait(false);
            var preInstallOnly = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            Assert(
                !preInstallOnly.HasPostInstallActivity,
                "fully pre-install LaunchPad history keeps clean detection on the fast path");

            await WriteLogAsync(
                installPath,
                [
                    Header(oldSession),
                    "6666-00:00:01:All files are up to date",
                    "**** Starting at definitely-not-a-date with plug-in 1.0.3.204 ****"
                ],
                cancellationToken).ConfigureAwait(false);
            var ambiguousTail = await service
                .InspectAsync(paths, appliedUtc, cancellationToken)
                .ConfigureAwait(false);
            Assert(
                ambiguousTail.HasPostInstallActivity,
                "an unplaceable appended session after known pre-install history fails detection closed");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                DeleteTree(root);
            }
        }
    }

    private static async Task AssertUnsafePathRejectedAsync(
        LaunchPadUpdateEvidenceService service,
        ProjectPaths paths,
        DateTimeOffset appliedUtc,
        DateTimeOffset sessionUtc,
        string loggedPath,
        CancellationToken cancellationToken)
    {
        await WriteLogAsync(
            paths.InstallPath,
            [
                Header(sessionUtc),
                "2222-00:00:00:Found 1 file(s) to update.",
                $"2222-00:00:01:Creating {loggedPath}",
                "Finished downloading 100 bytes in 0.1 seconds (1,000 bytes per second)"
            ],
            cancellationToken).ConfigureAwait(false);
        var evidence = await service
            .InspectAsync(paths, appliedUtc, cancellationToken)
            .ConfigureAwait(false);
        Assert(!evidence.IsCompleted, $"unsafe path is rejected: {loggedPath}");
        Assert(evidence.HasUnsafePath, $"unsafe path is identified: {loggedPath}");
        AssertEqual(0, evidence.ChangedFiles.Count, "unsafe session trusts no paths");
    }

    private static Task WriteLogAsync(
        string installPath,
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken) =>
        File.WriteAllLinesAsync(
            Path.Combine(
                installPath,
                LaunchPadUpdateEvidenceService.DownloadLogFileName),
            lines,
            cancellationToken);

    private static string Header(DateTimeOffset utc) =>
        $"**** Starting at {utc.ToLocalTime():ddd MMM d HH:mm:ss yyyy} with plug-in 1.0.3.204 ****";

    private static void AssertChanged(
        LaunchPadUpdateEvidence evidence,
        string relativePath,
        LaunchPadFileAction expectedAction)
    {
        Assert(
            evidence.TryGetChangedFile(relativePath, out var change),
            $"evidence contains {relativePath}");
        AssertEqual(expectedAction, change!.Action, $"action for {relativePath}");
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
