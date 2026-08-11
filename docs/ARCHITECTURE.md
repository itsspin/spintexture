# How SpinTexture works

SpinTexture is an offline texture-pack builder. It never runs inside EverQuest Legends and never changes rendering code.

## Pipeline

1. **Analyze (read-only).** SpinTexture identifies the selected EverQuest installation, discovers PFS/S3D/EQG archives and supported loose images, reads archive catalogs, and creates a plan for the chosen scope.
2. **Classify before decoding.** The planner separates diffuse/color art from normal maps, masks, UI/glyphs, effects, tiny controls, unsafe formats, and renderer sentinels. Protected entries remain byte-identical.
3. **Snapshot the exact source.** A build reads an immutable verified snapshot. If another SpinTexture pack is active, the source resolves to its exact managed original backup instead of upscaling an already enhanced archive.
4. **Reconstruct compatible textures.** Inputs are grouped into bounded directory batches so a Vulkan neural worker loads a model once for many textures. GPU inference remains serialized to avoid VRAM spikes while independent CPU preparation, validation, compression, and archive work use bounded parallelism.
5. **Preserve the source semantics.** The pipeline restores alpha separately, anchors color only when it improves the source match, keeps the legacy BC1/BC2/BC3 DDS family, generates verified mip chains, protects cutout coverage, and respects wrap versus clamp sampling.
6. **Fidelity gate.** The final reconstruction is reduced to the source grid and checked for palette/luminance drift, structural error, clipping, and lost or exaggerated edge energy. Aggressive output that fails retries through the Faithful route; a failed Faithful result is omitted and the original member remains unchanged.
7. **Rebuild and verify in staging.** SpinTexture reconstructs the complete archive outside the game directory, reopens it, and verifies member names, CRCs, dimensions, formats, mip counts, preserved hashes, and the PFS 4 GiB limit.
8. **Preview and compose.** Completed builds remain immutable in the Staged Pack Library. Compatible packs can be selected together without rerunning AI. Disjoint additions are promoted incrementally; conflicting whole archives are blocked.
9. **Install transactionally.** Installation creates exact SHA-256 backups and uses atomic replacement, post-write verification, rollback, and crash-recovery manifests. Restore returns every managed artifact to its recorded original bytes.

## Upscale routes

- **Faithful:** Real-ESRNet x4plus. Conservative restoration with minimal invented detail.
- **Texture HD (recommended):** PBRify Upscaler SPAN V4 for compatible material/diffuse textures. Painted graphics, animated art, and soft alpha use the Faithful route. Failed specialized output retries Faithful.
- **Maximum Detail:** Real-ESRGAN x4plus with its strongest reconstruction route and the same fidelity/fallback gates. Review this preset zone by zone.

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
