# Animation Maker 0.1.0-beta.3

Export a single H.264 MP4 with the animation on the left and its grayscale alpha mask on the right.

## Changes

- **Export MP4 · RGB + Alpha** uses the editor's final matte, crop, trim and frame rate. Choose High, Balanced or Small quality, with optional AAC audio.
- The desktop Player reconstructs transparency automatically for Animation Maker MP4 exports. Use **Interpret as RGB + Alpha** to preview external videos that use the same layout.
- Chroma keying supports magenta and other solid screen colors through the same eyedropper and matte preview.
- A PixiJS 8 WebGL shader and demo are included in the Windows packages under `delta-runtime/`. The [demo guide](https://github.com/Malasuerte94/animation-maker/blob/v0.1.0-beta.3/runtime/pixi-delta/README.md#mp4-rgb--alpha-demo) explains how to run it and integrate the shader.

## Playback

Standard video players show both halves. Animation Maker's Player or the PixiJS shader combines them for transparent playback. WebM remains available for delivery as VP9 with native alpha.

## Validation

89 .NET tests and 20 headless Chrome and Edge tests cover packed MP4 export, partial alpha, odd canvas dimensions, trim, audio, cancellation and PixiJS playback, alongside existing WebM and Delta tests. Mobile browser testing is pending.

## Downloads

- `AnimationMaker-0.1.0-beta.3-win-x64-setup.exe` for a per-user installation.
- `AnimationMaker-0.1.0-beta.3-win-x64-portable.zip` for a standalone folder. Extract all files and run `AnimationMaker.exe`.
- `AnimationMaker-PixiJS-Delta-0.1.0-beta.3.zip` for the reusable Delta runtime and TypeScript types.
- `FFmpeg-9.0.2-source.tar.gz` for the upstream source matching the bundled FFmpeg revision.
- `SHA256SUMS.txt` to verify the downloads.

The Windows packages include .NET, WinUI and FFmpeg. This beta is unsigned and requires Windows x64 and Direct3D 11. Processing is local.

The app and PixiJS code use the MIT license. Dependencies retain their own licenses, including GPLv3 for the separate FFmpeg executables.
