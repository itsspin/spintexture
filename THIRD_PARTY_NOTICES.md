# Third-party notices

SpinTexture release bundles include separately licensed, unmodified executables and model data. SpinTexture itself is independent from these projects.

- [Microsoft DirectXTex / texconv](https://github.com/microsoft/DirectXTex) - MIT License
- [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN) - BSD-3-Clause License
- [Real-ESRGAN ncnn Vulkan](https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan) - MIT License
- Microsoft Visual C++ OpenMP runtime (`vcomp140.dll`) - distributed under Microsoft's Visual Studio redistributable-code terms; see the [current Visual C++ Redistributable documentation](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)
- [ncnn](https://github.com/Tencent/ncnn) - BSD-3-Clause License and its bundled third-party notices
- [Upscayl ncnn Vulkan custom-model worker](https://github.com/upscayl/upscayl-ncnn/tree/20251207-174704) - AGPL-3.0 License
  - The exact corresponding source is available from the linked public tag at no charge.
- [PBRify Upscaler SPAN V4](https://github.com/Kim2091/PBRify_Remix) by Kim2091 - CC0-1.0
  - SpinTexture bundles a mechanically converted FP16 ncnn graph and weights derived from the public checkpoint.

Complete license texts for the bundled open-source components are included under `ThirdPartyLicenses` in every portable release and retained under `vendor/licenses` in the source repository. The Microsoft runtime terms are identified above. Exact versions, upstream source URLs, file sizes, and SHA-256 hashes are recorded in `vendor/manifest.json`; every release also includes a SHA-256 manifest for its own files.

No EverQuest or EverQuest Legends client assets are included in this repository or in SpinTexture releases.
