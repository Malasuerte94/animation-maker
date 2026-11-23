# Animation Maker 0.1.0-beta.4

## Changes

- Scroll over the Editor or Player preview to zoom around the pointer. Drag to pan, or choose **Fit** to recenter. Middle-drag pans while you edit a crop or mask.
- **Protected colors** keeps foreground shades during chroma keying. Sample colors from the source or enter up to 16 hex colors. Set the tolerance to zero for exact matches or increase it to include nearby shades.
- The editor starts at **Full resolution** with a **1024 px** maximum export frame height. It remembers your preview resolution, height limit, protected colors, tolerance and export format between sessions.
- Choose a format from the **Export format** dropdown to see its controls and export button. New clips start with **Balanced** video quality. Saved projects retain their chosen quality.

## Downloads

- `AnimationMaker-0.1.0-beta.4-win-x64-setup.exe` installs the app or updates an existing installation.
- `AnimationMaker-0.1.0-beta.4-win-x64-portable.zip` contains the standalone Windows app. Extract the folder and run `AnimationMaker.exe`.
- `AnimationMaker-PixiJS-Delta-0.1.0-beta.4.zip` contains the reusable PixiJS Delta runtime and TypeScript types.
- `FFmpeg-9.0.2-source.tar.gz` contains the source for the bundled FFmpeg revision.
- `SHA256SUMS.txt` contains checksums for the downloads.

The Windows packages are unsigned and require Windows x64 and Direct3D 11. Video processing runs locally.
