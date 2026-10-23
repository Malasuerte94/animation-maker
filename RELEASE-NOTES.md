# Animation Maker 0.1.0-beta.2

Smaller Delta manifests, denser texture packing and optional GPU Delta analysis for Windows x64.

## Changes

- Compact Delta v2 JSON stores numeric patch references and infers timing, checkpoint flags and patch dimensions. Classic atlas JSON is compact too.
- Tighter atlas packing tries several rectangular layouts and compares actual encoded sizes for Delta candidates. Zero-RGBA patch borders are trimmed, sparse independent frames can be split, and clear/replacement semantics are preserved.
- Delta analysis now offers Auto, CPU and GPU. DirectCompute handles change masks, bounds and error checks, shares processed textures, and keeps GPU reconstruction state. Recoverable GPU failures fall back to CPU.
- Auto measures longer clips before choosing GPU and avoids calibration on short or unknown-length clips.
- Exact-cost pruning skips candidates that cannot win. Atlas layout trials and final compression use at most two CPU workers, with deterministic selection and an image-memory budget.
- Export diagnostics record stage timings, GPU timestamps, transfer/codec counters, backend decisions and peak process memory.

## Compatibility

**Replace the reusable PixiJS runtime when using new Delta v2 exports.** The app and runtime in this release read both legacy v1 and compact v2 manifests. The previous runtime does not read v2.

Projects without an analysis setting default to Auto. GPU analysis does not move the entire exporter to GPU: decoding, image codecs and packing still have CPU work. Results and performance depend on the input and hardware.

## Validation

83 .NET tests passed in Release and 18 headless Chrome/Edge tests passed across WebGL2 and WebGPU. Checks include CPU/GPU equivalence, safe fallback, v1 compatibility, v2 validation, alpha, seeking and device restoration. Package checks and measured performance are recorded in `VALIDATION.md`.

On one 98-frame local comparison at unchanged WebP quality/tolerance, JSON decreased from 1,195,701 to 93,237 bytes and combined JSON/textures decreased from 2,114,873 to 1,032,665 bytes. Texture area decreased 29.6%; encoded image bytes increased 2.2%. These are results for one input, not guaranteed savings. The CPU and GPU backends produced byte-identical optimized assets on this sample.

## Downloads

- `AnimationMaker-0.1.0-beta.2-win-x64-setup.exe`: per-user installer.
- `AnimationMaker-0.1.0-beta.2-win-x64-portable.zip`: extract all files and run `AnimationMaker.exe`.
- `AnimationMaker-PixiJS-Delta-0.1.0-beta.2.zip`: reusable runtime, types and guide.
- `FFmpeg-9.0.2-source.tar.gz`: upstream source matching the bundled FFmpeg revision.
- `SHA256SUMS.txt`: release checksums.

The Windows packages include .NET, WinUI and FFmpeg. Windows x64 and Direct3D 11 are required. This beta is unsigned. Processing is local.

Application/runtime: MIT. Dependencies retain their terms, including GPLv3 for the separate FFmpeg executables; see the included notices.
