# Animation Maker 0.1.0-beta.1

First public beta for Windows x64, with MIT licensed application source and PixiJS delta runtime.

- GPU keying, despill, masks, crop, trim, project files and Undo/Redo.
- Classic PixiJS/Phaser atlases and transparent WebM exports.
- WebP quality control, default 75/100; optional PNG.
- **Delta Animation BETA:** reusable patches, replace/clear operations, checkpoints and patch tolerance.
- Delta exports contain only JSON and texture pages; the reusable PixiJS runtime ZIP is separate.
- Runtime supports play/pause, loop, speed, seek and freeze at a frame, with TypeScript types and a guide.
- Independent Player tab for delta/classic animations, video and images.

An author-reported example reduced a normal image from about **12 MB to 486 KB**, plus about **1 MB of JSON** (roughly **1.5 MB combined**). Further JSON/encoding optimization is planned. Results depend on content/settings; WebP and positive patch tolerance trade quality for size.

## Downloads

- `AnimationMaker-0.1.0-beta.1-win-x64-setup.exe`: per-user installer.
- `AnimationMaker-0.1.0-beta.1-win-x64-portable.zip`: extract all files and run `AnimationMaker.exe`.
- `AnimationMaker-PixiJS-Delta-0.1.0-beta.1.zip`: runtime, types and guide.
- `FFmpeg-9.0.2-source.tar.gz`: upstream FFmpeg source matching the bundled FFmpeg revision; build notes record external library versions/source references.
- `SHA256SUMS.txt`: release checksums.

The Windows packages include .NET, WinUI and FFmpeg. Windows x64 and Direct3D 11 are required. This beta is unsigned. Processing is local.

Application/runtime: MIT. Dependencies retain their terms, including GPLv3 for the separate FFmpeg executables; see the included notices.

60 .NET tests and 14 headless Chrome/Edge tests passed for this implementation. Package checks are recorded in `VALIDATION.md`. Broader manual/hardware testing remains necessary.
