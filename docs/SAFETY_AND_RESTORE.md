# Safety, installation, and exact restore

## What SpinTexture edits

SpinTexture writes to two controlled locations:

1. `%LOCALAPPDATA%\SpinTexture\Profiles\<install-id>` stores staged builds, manifests, logs, previews, and exact original backups.
2. The selected EverQuest Legends directory receives only the complete archive or supported loose-texture files listed in the checked staged composition. Native Graphics can separately edit `eqclient.ini` after showing the exact planned key changes.

SpinTexture does not edit `eqgame.exe`, `LaunchPad.exe`, DLLs, account files, UI XML, server data, or login tickets. It installs no runtime hook, proxy DLL, overlay, ReShade component, service, or driver profile.

## Safe first use

1. Keep an additional full copy of the game directory if disk space permits.
2. Close EverQuest and LaunchPad.
3. Analyze the client.
4. Build one selected zone with Texture HD and a 2,048 px cap.
5. Review several final-compressed previews at Fit and 1:1.
6. In Manage Staged Packs, check only the completed packs you want active and review the summary.
7. Install Selected Packs. SpinTexture verifies every staged payload and every live source before the first replacement.
8. Start the game with Play Enhanced EQ or its SpinTexture-created shortcut.

## Why Play Enhanced EQ is required

LaunchPad verifies complete game archives. When it sees a SpinTexture-enhanced archive, it can download and restore the official version before the game starts. Play Enhanced EQ starts the supported EverQuest `patchme` route without opening LaunchPad for that session, after SpinTexture checks that the installed pack is still healthy. It never captures or reuses a LaunchPad ticket or credentials.

Manual-login behavior can vary by server/account. Test Play Enhanced EQ with a small zone pack before committing to a full-client build.

For an official game update:

1. Use SpinTexture Restore.
2. Run LaunchPad and let the update finish.
3. Close EverQuest and LaunchPad.
4. Analyze the updated client again.
5. Rebuild only packs whose exact source archives changed.

Never force an old staged archive over a patched source. SpinTexture's SHA-256 preflight deliberately blocks that downgrade.

## Restore

Choose **Restore** in SpinTexture while EverQuest and LaunchPad are closed. Restore verifies the active transaction, returns every managed file to its exact backed-up original bytes, verifies the result, and retires the installed manifest. Unrelated game files are untouched.

Native Graphics has its own Restore action. It restores only the values that preset management changed and uses a three-way merge when the game has saved unrelated preferences since the preset was applied. A conflicting managed value is reported instead of silently overwritten.

If installation or restore is interrupted, reopen SpinTexture. Transaction manifests distinguish preparing, applied, restoring, restored, and rolled-back states so recovery can resume or fail closed. Do not delete the profile's `Backups` directory while a pack is active.

## Staged builds are reusable

Closing SpinTexture does not delete a completed staged pack. You can later combine compatible builds, add a disjoint zone incrementally, reinstall after LaunchPad restores official files, or create an immutable targeted repair without rerunning successful neural work. Incomplete or corrupt payloads are never installable.
