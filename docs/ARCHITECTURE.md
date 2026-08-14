# How SpinTexture works

SpinTexture is an offline texture-pack builder. It never runs inside EverQuest Legends and never changes rendering code.

## Pipeline

1. **Analyze (read-only).** SpinTexture identifies the selected EverQuest installation, discovers PFS/S3D/EQG archives and supported loose images, reads archive catalogs, and creates a plan for the chosen scope.
2. **Classify before decoding.** The planner separates diffuse/color art from normal maps, masks, UI/glyphs, effects, tiny controls, unsafe formats, and renderer sentinels. Protected entries remain byte-identical.
3. **Snapshot the exact source.** A build reads an immutable verified snapshot. If another SpinTexture pack is active, the source resolves to its exact managed original backup instead of upscaling an already enhanced archive.
4. **Reconstruct compatible textures.** Inputs are grouped into bounded directory batches so a Vulkan neural worker loads a model once for many textures. GPU inference remains serialized to avoid VRAM spikes while independent CPU preparation, validation, compression, and archive work use bounded parallelism. Graphic Painted builds then apply one deterministic, recorded theme. “Follow each zone” resolves exact reviewed archive names to a concrete Light Storybook or Dark Gothic mood and otherwise fails to Classic Painted; it never infers mood from filename fragments or pixels. The mood transform is locally attenuated around vivid and bright source accents so a dark-zone assignment cannot flatten authored magic, signage, metal highlights, or contrasting decoration into one global grade.
5. **Preserve the source semantics.** The pipeline restores alpha separately, anchors color only when it improves the source match, keeps the legacy BC1/BC2/BC3 DDS family, generates verified mip chains, protects cutout coverage, and respects wrap versus clamp sampling.
6. **Fidelity gate.** The final reconstruction is reduced to the source grid and checked for palette/luminance drift, structural error, clipping, and lost or exaggerated edge energy. Aggressive output that fails retries through the Faithful route; a failed Faithful result is omitted and the original member remains unchanged.
7. **Repair identity gate.** A repair inherits the completed pack's exact preset and painted theme and starts from the SHA-256-verified staged baseline. Painted repairs accept regenerated pixels only when the processing route proves that same recorded profile; fallback, unsupported, legacy-algorithm mixing, or newly invalid prior output aborts the immutable replacement instead of publishing a hybrid pack. Exact-original sky, celestial, water, and user-selected preservation remain explicit safety policies.
7. **Rebuild and verify in staging.** SpinTexture reconstructs the complete archive outside the game directory, reopens it, and verifies member names, CRCs, dimensions, formats, mip counts, preserved hashes, and the PFS 4 GiB limit.
8. **Preview and compose.** Completed builds remain immutable in the Staged Pack Library. Compatible packs can be selected together without rerunning AI. Disjoint additions are promoted incrementally; conflicting whole archives are blocked.
9. **Relocatable staged storage.** The large completed-pack library can be moved independently of backups and transaction metadata. Migration copies into a SpinTexture-owned destination, rejects reparse points and unrelated destination contents, SHA-256 verifies the complete file tree, commits the profile setting atomically, and only then attempts to remove the old managed copy.
9. **Install transactionally.** Installation creates exact SHA-256 backups and uses atomic replacement, post-write verification, rollback, and crash-recovery manifests. Restore returns every managed artifact to its recorded original bytes.

## Upscale routes

- **Faithful:** Real-ESRNet x4plus. Conservative restoration with minimal invented detail.
- **Texture HD (recommended):** PBRify Upscaler SPAN V4 for compatible material/diffuse textures. Painted graphics, animated art, and soft alpha use the Faithful route. Failed specialized output retries Faithful.
- **Material Detail:** Real-ESRGAN x4plus with test-time augmentation and the same fidelity/fallback gates. This is generated texture microdetail, not a PBR material or geometry replacement; review it zone by zone.
- **Graphic Painted Fantasy:** the SHA-256-pinned Real-ESRGAN x4plus Anime reconstruction followed by SpinTexture's deterministic graphic-paint finish and one concrete Classic Painted, Light Storybook, Dark Gothic, or Comic Ink palette treatment. The finish follows the existing wrap-or-clamp sampling policy for each asset. Eligible diffuse art gains broader color/value planes and restrained structural accents; alpha remains exact, unsafe renderer assets stay protected, and failed outputs fall back safely. The reviewed zone map is an exact allowlist with Classic fallback. It cannot add mesh outlines or replace the game renderer.

Environmental sky and authored translucent water remain hard compatibility boundaries. `sky.s3d`, native sky resources, and WLD-linked translucent material sets are preserved or restored from verified originals rather than resized. A future water experiment requires successful WLD resolution, an exact baseline pack, complete animation-family atomicity, and unchanged dimensions, format, alpha, palette, timing, mip layout, and material metadata; it cannot be composed as an independent conflicting copy of the same zone archive.

Each completed PFS build records a searchable safe-texture index. A review-driven revision can restore a selected member from the verified original or bypass reuse and reprocess only that member with a selected preset. All other valid enhanced members are raw-carried from the verified immutable baseline. Loose spell-effect art uses a separate conservative allowlist; packed sheets and technical/control resources remain protected.

The selected maximum dimension is a ceiling, not a command to make every file 4K. SpinTexture chooses one meaningful neural enlargement and preserves aspect ratio.

## Hardware behavior

Neural inference uses the bundled ncnn Vulkan workers and therefore supports compatible AMD, NVIDIA, and Intel graphics adapters. SpinTexture uses bounded batches and one GPU inference group at a time; launching many competing Vulkan processes is usually slower and can exhaust VRAM. Faster GPUs complete each batch sooner. CPU preparation and post-processing use 1-4 bounded lanes selected from logical processor count and available memory, while archive reconstruction remains streaming and deterministic so higher-core-count systems gain throughput without making lower-memory systems unstable.

On the development RX 9070 XT / 16-logical-CPU reference system, the final adaptive queue improved small-texture PBRify batches by 7.3% and Faithful Real-ESRNet batches by 9.0%. One bounded 64-texture launch was 41.4% faster than two 32-item launches, and four-lane DDS encoding was 50.6% faster than serial encoding. Compared neural outputs were SHA-256 identical. These are reference measurements rather than a promise for every driver or texture set; large images automatically retain the more conservative memory profile.

There is no CPU-only neural backend in the current release. A current Vulkan-capable driver is required.

## Native graphics settings

Native Graphics / Lighting is separate from texture reconstruction. It verifies that the installed client exposes the expected renderer controls, previews exact changes, backs up `eqclient.ini`, and atomically manages only:

- `[Defaults] Shadows`
- `[Defaults] MultiPassLighting`
- `[Defaults] PostEffects`
- `[Defaults] Bloom`
- `[Options] ShadowClipPlane`

Balanced enables native shadows and preserves the user's other baseline values. Cinematic enables the supported advanced-lighting, post-effect, and bloom paths and sets the verified native shadow-distance maximum to `100`. This is not an injector or post-process DLL, and it does not manufacture depth of field. Static object draw distance and zone geometry are outside these settings.
