# SpinTexture for EverQuest Legends

[![CI](https://github.com/itsspin/spintexture/actions/workflows/ci.yml/badge.svg)](https://github.com/itsspin/spintexture/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/itsspin/spintexture?display_name=tag)](https://github.com/itsspin/spintexture/releases/latest)
[![Windows x64](https://img.shields.io/badge/platform-Windows%20x64-5aa9ff)](https://github.com/itsspin/spintexture/releases/latest)

SpinTexture is a portable Windows texture-pack builder for EverQuest Legends. It reconstructs clearer, higher-resolution world, character, armor, and equipment textures while preserving the original art direction, legacy container formats, alpha behavior, and safe fallbacks. No EverQuest assets are included with the program.

It does **not** inject a DLL, hook Direct3D, install ReShade, modify the game executable, contact game servers, or read credentials. Texture enhancements are staged outside the client, reviewed, then installed as complete verified archives with exact backups. The optional Native Graphics presets make reversible edits to five supported `eqclient.ini` values only.

| Enhanced characters, armor, and equipment | Enhanced world gameplay |
| --- | --- |
| ![Enhanced characters and equipment](docs/media/enhanced-characters-and-equipment.jpg) | ![Enhanced Freeport gameplay](docs/media/enhanced-freeport-gameplay.gif) |

![Enhanced skeleton texture comparison](docs/media/enhanced-skeleton-comparison.jpg)

### Interactive original vs enhanced comparison

[![Open the SpinTexture comparison gallery](docs/media/comparisons/characters-enhanced.png)](https://itsspin.github.io/spintexture/)

Jump directly to the **[characters + equipment comparison](https://itsspin.github.io/spintexture/#characters)**, **[Lavastorm world + equipment comparison](https://itsspin.github.io/spintexture/#lavastorm)**, or **[Nagafen's Lair world + creature comparison](https://itsspin.github.io/spintexture/#nagafen)**.

**[Open the interactive comparison gallery](https://itsspin.github.io/spintexture/)** to drag or swipe between unchanged original client screenshots and the same scenes using SpinTexture-enhanced world, character, and equipment textures. Each comparison is keyboard accessible with the arrow keys. Small pose and background-creature differences come from the live game; no capture was retouched to manufacture detail.

## Download

Download the latest `SpinTexture-<version>-win-x64.zip` from [GitHub Releases](https://github.com/itsspin/spintexture/releases/latest), verify the accompanying SHA-256 file, and extract the whole folder. Keep `Tools` beside `SpinTexture.exe`.

Read [How SpinTexture works](docs/ARCHITECTURE.md) for the processing pipeline and [Safety, installation, and exact restore](docs/SAFETY_AND_RESTORE.md) before a large full-client build.

The safe first run is **Selected zone → lavastorm → Texture HD → 2,048 px → Build Staged Pack**. Staging writes only to SpinTexture's managed workspace. If another SpinTexture pack is active, build inputs come from its exact verified original backups rather than the enhanced live archives.

## Using the app

1. Make a full copy of the EverQuest Legends directory as an extra precaution.
2. Extract the complete SpinTexture release folder; keep the `Tools` folder beside `SpinTexture.exe`.
3. Run `SpinTexture.exe` and choose the directory containing `eqgame.exe`.
4. Select **Analyze Client**. This is read-only.
5. For a quick proof, select **Selected zone** and **lavastorm**.
6. Choose an art direction and click **Build Staged Pack**. Texture Review opens inside the same SpinTexture window when the staged output is ready.
7. Inspect several rock, ground, structure, vegetation, and effect textures. Start with **Fit**, then choose **1:1** to judge the actual output pixels. New builds also include a complete searchable index of safely editable archive textures. From the Staged Pack Library, choose **Keep original** or a **Redo** art direction for any reviewed entry; SpinTexture creates a new immutable pack and reuses every unselected result.
8. Choose **Packs** in the top navigation. Filter by Zones, Characters & Equipment, Spell Effects, or World / Combined; check every completed pack you want active, then choose **Install Checked Packs**. SpinTexture verifies and composes disjoint packs without running AI again. Close EverQuest and LaunchPad before installing.
9. For normal play, use **Play Enhanced EQ** or the install-specific desktop shortcut SpinTexture creates. The shortcut starts SpinTexture in a small verification mode and then starts `eqgame.exe patchme`; it does not upscale or copy archives on each launch.
10. If this Legends client presents EverQuest's manual login screen, sign in there. Manual `patchme` authentication support can vary and should be tested with the target server/account. SpinTexture never reads, stores, forwards, or reuses a LaunchPad login ticket or account credentials.
11. Use **Restore** to put the exact backed-up originals back.

Large completed packs do not have to remain on the Windows system drive. Choose **Storage** in the top navigation, select a parent folder on another internal or external drive, and choose **Move + Verify Pack Library**. SpinTexture creates its own install-specific folder, copies every staged file, verifies SHA-256 hashes, and switches the saved location only after the complete destination matches. Original backups, recovery records, settings, and the game client do not move. If cleanup of the old copy is blocked by Windows, the verified new location remains active and the app reports the old folder that can be removed later.

LaunchPad validates asset archives and can restore customized files whenever it starts. Do not use LaunchPad for everyday enhanced play. When the game actually needs an update, restore the originals first, run LaunchPad, then analyze the patched client again. SpinTexture reuses a staged pack only while every source archive still matches its build-time SHA-256; if a patch changed an archive, a safe rebuild against the patched client is required. The credential-free `patchme` route uses EverQuest's manual login path and should be tested with the target server/account before committing to a very large full-game build.

## Before/after review

Each build captures up to 24 representative eligible textures. The left image is the exact original texture enlarged with normal cubic texture filtering; the right image is decoded from the final compressed DDS that SpinTexture staged for EverQuest. Both use the same dimensions and synchronized zoom. This is a fairer comparison than nearest-neighbor enlargement, which exaggerates block-shaped edges that are not representative of normal in-game filtering.

**Fit** is a downsampled overview when the texture is larger than the gallery surface. It is useful for checking whether the art still looks like EverQuest, but it can hide fine detail. **1:1** maps one enhanced texture pixel to one display pixel and is the honest mode for inspecting cracks, fibers, compression cleanup, and edge quality.

The gallery identifies the archive and texture name and shows original and enhanced dimensions. Previewing is read-only and does not install the staged pack. A selected-zone build is the quickest way to decide whether a preset is worthwhile before committing time and disk space to the whole world.

## Staged Pack Library and targeted repair

Every completed build remains in the install profile's staged-pack library. **Packs**, **Review**, **Graphics**, and **Storage** are sections of the main SpinTexture window rather than separate utility windows. The Packs section can filter by scope and shows each build's style, date, archive count, size, integrity state, archive contents, and available before/after review. A **checkbox** means the pack will be included in the next install; clicking the rest of a card focuses it for details, preview, repair, or deletion. Advanced Ctrl/Shift multi-row tools stay collapsed unless requested. Packs with disjoint archive paths are composed by hard link when possible, so combining a large character pack with one or more zone packs does not rerun the upscaler or duplicate the staged payload. Conflicting versions of the same complete archive are blocked instead of silently choosing one.

When the checked selection is an additive superset of the active pack, SpinTexture installs and backs up only the newly added archives; already-active character, equipment, or world archives are not copied again. A first install, a removal, or a conflicting replacement still uses the full verified transaction path.

**Repair Missing in Selected** creates a new immutable replacement for a character/equipment build. It compares each member of the verified original archive with the prior staged archive: already enhanced members are reused byte-for-byte, while only unchanged entries that are newly supported or previously failed a safety/fidelity gate are processed. The original completed pack is never edited or deleted. This is the recovery path for an older long-running build that preserved classic indexed race/armor art; it does not throw away the textures that already succeeded.

**Repair Source Mismatch** is a separate recovery path for a World or zone pack that was accidentally built while older enhanced archives were active. It exact-verifies the completed pack and managed install provenance, reuses every unaffected complete archive, and rebuilds only archives proven to have used a prior SpinTexture output as their source. Unknown game-patch changes, missing backups, and corrupt provenance are blocked instead of guessed. The original staged pack remains unchanged.

**Upgrade Cutout Compatibility** appears once for older completed Characters + Equipment, World, combined World + Characters, and selected-zone packs. It creates a new immutable replacement, raw-copies the prior compressed chunks for valid opaque enhancements, leaves source-identical entries untouched, and regenerates only previously enhanced alpha-tested textures without generated soft-alpha mip levels that can cross the legacy renderer's cutoff as the camera angle changes. Fresh builds already use the single-level cutout policy and do not need this upgrade.

## Presets and “4K”

- **Original Clarity (Faithful)** uses the conservative Real-ESRNet model. It cleans scaling/compression artifacts with minimal invented detail, so its improvement can be subtle at normal viewing size.
- **Texture HD** is the recommended world-texture route. It uses PBRify Upscaler SPAN V4 for diffuse terrain and material textures, then applies palette anchoring only when it measurably improves the match back to the source. Painted graphics, soft alpha, and animated effects use the more restrained Real-ESRNet route. Source alpha is restored separately; binary cutout coverage and fully opaque/transparent endpoints remain protected. If the specialized route fails its worker or fidelity checks, SpinTexture retries that texture with faithful Real-ESRNet instead of substituting a more aggressive generic GAN.
- **Material Detail** uses Real-ESRGAN x4plus with test-time augmentation for the strongest generated microdetail route. It can make rock, metal, and cloth look more textured, but it does not create true physically based materials or new geometry. It is the slowest and highest-variance option, so review it zone by zone.
- **Illustrated / Clean Painted** uses the official Real-ESRGAN x4plus Anime model for flatter painted detail, cleaner shapes, and less photographic surface noise. It can move texture art in a cel-shaded direction, but it cannot add cartoon outlines around 3D models or change lighting/shader geometry. It is preview-first: the same format, alpha, palette, mip, fidelity, and faithful-fallback protections remain active.
- **Rustic Painted Fantasy** starts with the same validated illustrated reconstruction, then applies SpinTexture's bounded watercolor-inspired grade: warmer shadows, olive-shifted foliage, restrained saturation, and subtle painted tone planes. Strength is reduced for characters, cutouts, and spell art. The grade preserves alpha exactly and must pass separate palette, structure, edge, and clipping limits; otherwise that texture falls back to Original Clarity. It does not copy or redistribute another texture pack and it does not invent new geometry or toon outlines.

The model name, expected visual direction, and relative performance cost are shown directly under the selected style. For an honest example, build one **Selected zone**: SpinTexture's in-app Review section compares the actual source texture with the final compressed texture that will be installed. Safety fallback is intentional, so a protected or unstable texture may remain original or use Original Clarity even when a stronger style is selected.

SpinTexture does not force every image to 4,096×4,096. The selected size is a ceiling, while the neural model performs one meaningful 4× linear restoration: 256 becomes 1,024; 512 becomes 2,048; 1,024 can become 4,096. Aspect ratio is preserved. Repeatedly enlarging a 256 texture to 4,096 would create 16× dimensions without recovering trustworthy detail, while greatly increasing memory and archive sizes.

## What it protects

- Reads PFS/S3D/EQG members by their actual file signatures; mislabeled `.bmp` entries containing DDS data are handled correctly.
- Preserves the original legacy Direct3D 9 DDS family (BC1/DXT1, BC2/DXT3, or BC3/DXT5) and writes legacy headers.
- Generates and verifies exact mip chains. Opaque textures retain their full chain; alpha-tested cutouts stop at the last level whose width and height are both at least 4 pixels, avoiding legacy alpha-test collapse in 2x2 and 1x1 foliage mips. Tileable terrain uses wrap-aware sampling, while objects, decals, cutouts, and loose textures use clamped borders to prevent opposite-edge bleed.
- Preserves alpha separately, detects binary cutouts and soft translucency from decoded pixels, keeps 0/255 alpha endpoints, edge-dilates hidden cutout color to reduce foliage halos, and automatically uses the conservative model for soft alpha.
- Detects fully transparent legacy DDS renderer-control textures before reuse or inference and preserves them byte-for-byte. This protects Invisible-Man/enchanter animation materials from becoming opaque magenta.
- Reconstructs a conservative subset of classic 8-bit BI_RGB character and armor bitmaps while retaining the original indexed representation, palette, header semantics, and keyed border mask. Tiny, low-color, compressed, or structurally ambiguous indexed bitmaps remain original.
- Skips normal/data maps, numbered masks, UI/glyph assets, packed palettes/atlases, unusual arrays/cubemaps, premultiplied DXT2/DXT4, unsafe formats, tiny textures, and likely sprite strips. Spell/effect assets remain protected in ordinary scopes.
- **Spell effects** is a separate preview-first scope for supported loose single-image art in the client's effect directories. Animation sheets, flipbooks, strips, technical maps, unsupported aspect ratios/containers, fully transparent controls, and ambiguous resources remain original. Soft-translucent particles retain their original alpha plane and use the Faithful / Original Clarity reconstruction instead of the selected stylization.
- Routes ambiguous lava/fire/water animation frames through the conservative model to reduce frame-to-frame shimmer.
- Rebuilds complete archives in staging with bounded streaming memory, verifies every preserved member with SHA-256, reopens the result, and checks names, CRCs, formats, dimensions, and exact mip counts.
- Measures each reconstruction after reducing it back to the exact source grid, rejecting excessive palette/luminance drift, structural error, lost or exaggerated edge energy, and new clipping. A non-faithful result retries with Real-ESRNet; a failed faithful enhancement is omitted while its original texture remains byte-identical.
- Preflights the legacy PFS 4 GiB address limit before lengthy archive processing.
- Omits archives whose staged bytes are identical to the source.
- Uses exact source snapshots during Build so the builder never receives a writable live-client path. Active enhanced artifacts resolve to their verified managed originals, with the expected SHA-256 checked again at snapshot time.
- Applies with a per-install lock, atomic file replacement, verified backups, rollback, and resumable crash recovery.

By default, managed builds and backups live under `%LOCALAPPDATA%\SpinTexture\Profiles\<install-name>-<hash>`. They are separate from the EverQuest directory. The **Storage** section can move only the large reusable `Staging` library to a SpinTexture-owned folder on another drive; verified original backups and recovery metadata remain in the local install profile so Restore is not coupled to removable pack storage.

## Scope notes

- **Selected zone** is fastest and safest for visual review.
- **World only** includes discovered zone archives, classic main/object archive pairs, and shared furniture/terrain/sky/plant packages.
- **Characters + equipment only** includes client-cataloged playable races and biological/NPC mob models, player-character packs, classic indexed race and worn-armor atlases, dedicated armor/robe/equipment archives, and verified weapon textures without adding zone or shared-world assets. Mixed packs are filtered to equipment material dependencies; unrelated housing and world entries remain untouched.
- **Spell effects only** operates on the conservative loose-effect allowlist and does not touch UI, zone archives, characters, or executable/shader code.

### Per-texture review and redo

SpinTexture does not generate thousands of PNG pairs during every build. That would add large conversion and storage costs. Instead, it saves representative full before/after comparisons plus a complete searchable index of every safely editable PFS member. Open a pack's gallery from **Manage Staged Packs**, find a texture by archive/member name, and choose:

- **Use pack result** — retain the existing enhanced member.
- **Keep original** — restore that exact member from the SHA-256-verified original archive.
- **Redo** — rerun only that member with Original Clarity, Texture HD, Material Detail, Illustrated / Clean Painted, or Rustic Painted Fantasy.

The revision is a new full replacement pack. Unselected enhanced members and their stored archive chunks are carried from the exact-verified baseline; the earlier pack is never modified or deleted. Protected technical/control textures cannot be forced through a color model. Loose spell-effect packs currently use whole-scope rebuilds; member-level revision is limited to PFS/S3D/EQG archive packs.
- **World + characters** is the exact union of **World only** and **Characters + equipment only**.
- **All safe textures** checks every PFS archive plus supported loose DDS/BMP/TGA images; its classifier still skips protected content.

A full-world build can take a long time and consume substantial disk space. Start with one zone, use a 2,048 px cap, and keep at least several tens of gigabytes free. Keep LaunchPad closed during the build; if an external update changes a source later, install preflight blocks the stale pack before any live write. Compatible textures are sent through bounded directory batches, so the neural model is loaded once for many inputs instead of once per texture. Batches are capped by item count and neural-output pixels, and GPU groups remain serial to avoid exhausting VRAM. Lossless PNG handoff is performed in-process, removing two converter launches per enhanced color texture.

Fidelity-gate retries are collected into bounded Faithful batches instead of starting one native worker per rejected texture. On systems with at least 12 logical processors, sufficiently small batches can use the validated `1:3:3` ncnn load/process/save queue; larger work retains the lower-memory `1:2:2` profile, and any accelerated-worker failure retries the same model conservatively before normal fallback. CPU preparation and final encoding use 1-4 bounded, memory-aware lanes. Estimates use the latest equivalent completed build when one exists; the app labels that range as measured and shows the prior archive count and staged size. New configurations retain a conservative fallback estimate until enough matching local history exists.

The neural workers require a Vulkan-capable AMD, NVIDIA, or Intel graphics adapter and current graphics driver. DirectXTex decoding, mip generation, DDS compression, and archive reconstruction also use the CPU; there is currently no CPU-only neural inference mode.

## Native graphics and cinematic lighting

The **Graphics** section manages EverQuest Legends' own real-time stencil shadows through `eqclient.ini`; it does not install ReShade, a proxy DLL, an overlay, or a runtime hook. **Balanced** changes only the native `Shadows` setting and preserves the pre-preset shadow distance, Advanced Lighting, Post Effects, and Bloom values. **Cinematic** also enables the client's Advanced Lighting, Post Effects, and Bloom Lighting paths and sets the verified native `ShadowClipPlane` control to `100` (maximum). SpinTexture never modifies, filters, or rescales UI assets. Advanced Lighting targets the 3D world, while the exact screen-space composition boundary of EQL's optional native Post Effects and Bloom paths is controlled by the game and is not promised to leave every HUD pixel identical. Depth of field is not enabled because this client does not expose a native DOF renderer or supported option.

SpinTexture previews every exact setting change, creates a verified original backup, writes atomically only while EverQuest and LaunchPad are closed, and can restore the managed values without erasing unrelated preferences the game saved later. The Legends executable and shipped Advanced Display XML both expose `ShadowClipPlane`, even when that window is inaccessible in the current UI. Cinematic therefore uses the verified maximum value of `100`; Balanced leaves the user's original value or absence untouched. This affects cast-shadow range, not the visibility range of static zone meshes. General Far Clip is a separate setting, and zone-authored tree/object culling cannot safely be overridden through a verified Legends INI key. Maximum-distance native shadows can be expensive, so start with Balanced unless the extra range is worth the frame-rate cost.

The Native Graphics window also documents optional per-game GPU finishing: AMD Adrenalin can apply Display Color Enhancement and Radeon Image Sharpening, while NVIDIA's supported-game Freestyle path can offer RTX Dynamic Vibrance and RTX HDR. SpinTexture does not write undocumented vendor driver profiles. These filters see the completed frame and may therefore affect UI colors or sharpness; neither vendor can reconstruct correct live depth of field or new geometry-cast shadows without game-provided scene depth.

## Important limitations

Upscaling can reconstruct clearer source imagery, reduce block artifacts, and make close-up surfaces much crisper. It cannot add polygons, repair low-detail geometry, change draw distance, or fully correct stretched UV mapping. The severe stretch visible on some Lavastorm slopes is partly a geometry/UV issue, so the texture becomes sharper but the stretch itself remains.

AI reconstruction is not perfectly lossless. Review staged results before installation, especially animated effects and unusual transparent assets. SpinTexture contains no injection, hooking, anti-cheat bypass, or server interaction.

## Legal notice

SpinTexture is an independent fan utility. EverQuest and EverQuest Legends are trademarks of Daybreak Game Company LLC. Client modification or third-party utilities may be restricted by the applicable license or server rules. Users are responsible for obtaining any required authorization and complying with those terms. The app requires an explicit acknowledgment before installation.

## Building from source

Requirements: Windows x64 and the .NET 9 SDK. The pinned official DirectXTex and Real-ESRGAN ncnn Vulkan files are under `vendor`; their licenses and hashes are included.

```powershell
# Run from the cloned repository root.
.\build.ps1
```

The script restores, builds with warnings treated as errors, runs archive/native/transaction tests, publishes a self-contained app, and writes a release hash manifest under `publish\SpinTexture-win-x64`.

### Publishing a Windows release

Maintainers can publish without editing workflow files:

1. Merge the intended source to `main` and confirm CI is green.
2. Open **Actions > Release > Run workflow**.
3. Enter a new version such as `1.2.0` (a leading `v` is also accepted).
4. Run the workflow from `main`.

The workflow validates the version and unused tag, runs the complete Release build and self-tests, embeds the version in the app, verifies the portable ZIP, and creates a GitHub Release containing the Windows x64 ZIP and its SHA-256 checksum. Hosted runners skip only the physical Vulkan-device smoke test because they do not expose a supported GPU; command, batching, archive, fidelity, rollback, and packaging tests still run. The full native GPU smoke test remains part of normal local `build.ps1` runs.

Verified against the local Legends client during development: 2,270 PFS archives, 60,654 packed textures, 5,423 loose images, real Lavastorm staged builds, and byte-identical live archive hashes before/after staging. On the reference RX 9070 XT system, the same 1,024px Texture HD Lavastorm build improved from 76.8 seconds to 21.3 seconds (3.60x faster) while enhancing the same 38 textures and preserving the same five protected assets. No live client files were modified by those pilot builds.
