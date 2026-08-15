# Experimental: external artistic painted worker

Graphic Painted Fantasy normally uses SpinTexture's built-in painterly
stylization on top of a neural reconstruction. For the highest possible
"repainted by an artist" quality, SpinTexture can instead hand the
reconstruction step to an **external artistic worker** you provide — typically
a Stable-Diffusion img2img pipeline. This is experimental and entirely
optional: when no worker is configured, nothing changes.

## How SpinTexture uses the worker

When a worker is configured and you build with **Graphic Painted Fantasy**:

1. SpinTexture prepares each eligible texture exactly as usual: cutout RGB is
   dilated under transparency, and a seam-safe wrapped border is added so
   tiling world materials stay seamless.
2. The worker is invoked once per batch directory of PNGs (model stays warm).
3. The output is cropped, the **original alpha plane is restored byte-exact**,
   the painted fidelity gates check for catastrophic output, and the selected
   painted theme is applied on top. **Classic painted applies no palette
   treatment at all** — choose it to see the diffusion art style exactly as
   generated; the other themes (Follow each zone / Light Storybook / Dark
   Gothic / Comic Ink) add their color grade on top of it. The built-in
   painterly stylizer and its sliders are bypassed — the worker owns the art
   direction.
4. Any failure — the worker won't start, output files are missing or the
   wrong size, or a texture fails the fidelity gate — falls back safely to
   the built-in painterly stylization for that build or texture. A build
   never fails or silently ships unstyled textures because of the worker.

## Worker contract

SpinTexture runs:

```
worker -i <inputDirectory> -o <outputDirectory> -s 4 -f png
```

Requirements:

- Read every `*.png` in the input directory; write one PNG per input to the
  output directory with the **identical file name**.
- Output must be **exactly 4x** the input width and height.
- Preserve the image layout: the outer border of each input is seam padding —
  do not crop, letterbox, or outpaint.
- **Deterministic**: the same input must produce the same output bytes
  (fix your seed, sampler, and thread count). Repairs and resumed builds
  rebuild individual textures and must reproduce the recorded pack exactly.
- Exit code 0 on success; any other exit code triggers the safe fallback.

SpinTexture may also write a `batch-meta.json` sidecar into the input
directory with optional per-file art direction: `promptSuffix` (material and
zone vocabulary composed deterministically from the texture's name and its
zone archive) and `denoiseScale` (below 1.0 for water, lava, flame, and
other animated-surface families so consecutive animation frames stay
coherent). The generated one-click worker honors it; a custom worker that
only enumerates `*.png` can safely ignore it.

## One-click setup (recommended)

The **Set Up Diffusion Repaint (~2.9 GB)** button in the Graphic Painted
panel installs everything automatically: the stable-diffusion.cpp **Vulkan**
build (AMD, NVIDIA, and Intel GPUs — no CUDA, no Python), the DreamShaper 8
painterly checkpoint, and ControlNet v1.1 Tile. Every download is pinned to
an exact size and SHA-256 and refused on any mismatch; an interrupted setup
resumes safely when re-run. After download, SpinTexture generates the worker
scripts and verifies them on your PC by repainting a test image twice —
checking exact 4x output and byte-identical determinism — before the worker
is enabled.

### Art styles

Once installed, the **Art Style** dropdown in the Graphic Painted panel
switches between curated diffusion recipes:

- **Painted Fantasy** — bold hand-painted brush work, rich saturated color.
  The balanced default.
- **Epic Cinematic** — dramatic concept-art lighting, atmospheric depth, rich
  color grading. The most transformative look.
- **Dark Oil Painting** — old-master chiaroscuro, heavy impasto, muted
  ominous palette. Made for dungeons and dark cities.
- **Storybook Watercolor** — soft washes, gentle ink lines, warm whimsical
  color for pastoral zones.
- **Comic Ink** — cel-shaded planes, strong ink lines, vivid flat color.

Every style shares the same seed, step count, and resolution bound, so
switching styles changes the art direction — not build time or determinism.
Advanced settings (prompt, denoise strength, steps, seed, maximum diffusion
resolution) live in `Tools\artistic-worker\worker-config.json`; hand-editing
it past a recipe shows as **Custom** in the dropdown and is used as-is.
Re-running setup never resets your style choice.

### Full-resolution repaint (tiled)

By default the diffusion pass is bounded (1152px edge) and larger textures
are bicubically upscaled to their final size, which softens painted detail
on 2K/4K textures. The **Full-resolution repaint** checkbox paints oversized
textures in overlapping 1152px tiles instead — every pixel is diffusion
detail — and blends the tiles with wide linear ramps so no tile seams show.
The worker itself is unaware of tiling (each tile is an ordinary contract
file), the tile grid is a pure function of the texture's size so repairs
reproduce it exactly, and a 4K texture becomes ~25 diffusion passes: expect
builds several times slower with it on.

**Caveat:** the chosen style is part of the worker, not the pack recording.
Keep the style unchanged between building a pack and repairing it —
repairing individual textures under a different style would repaint them in
the new look. To change styles, build a fresh pack.

## Installing a custom worker manually

Either set the environment variable:

```
SPINTEXTURE_ARTISTIC_WORKER=C:\path\to\worker.bat
```

or place it at `Tools\artistic-worker\worker.bat` (or `.exe` / `.cmd`)
next to `SpinTexture.exe`. The build log notes when a worker is active, and
each texture's processing route records that the external artistic
reconstruction was used.

## Reference recipe: Stable Diffusion img2img (ComfyUI or A1111)

A good starting point for the "hand-painted fantasy" look:

- **Checkpoint:** an SDXL model with strong stylized/painterly output.
- **Mode:** img2img at denoise **0.35–0.5** — high enough to repaint
  surfaces, low enough to keep the texture's layout and UV mapping intact.
- **ControlNet Tile** (weight ~0.6–0.9) to lock structure so text, trim, and
  masonry stay put while surfaces get repainted.
- **Prompt:** describe the material, not the scene — e.g. `hand-painted
  fantasy game texture, visible brush strokes, rich saturated color, stylized`.
- **Fixed seed**, fixed sampler, batch over the input directory, save with
  original filenames at 4x.
- Upscale inside the same pipeline (e.g. latent upscale or a model upscale
  pass before img2img) so the final output is exactly 4x.

Expect roughly seconds per texture on a modern GPU — a full world build can
take hours, so trial a single zone first. Review results in the Staged Pack
Library preview before installing; everything remains protected by the same
verification, backup, and restore guarantees as every other build.
