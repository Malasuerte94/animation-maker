# Animation Maker 0.1.0-beta.5

## Automatic updates

Install Beta 5 once using the Windows installer, or extract the new portable ZIP. Earlier versions require this manual update.

Animation Maker now checks the latest official GitHub release at startup and every six hours while open. When a newer version is available, it downloads the installer in the background and verifies its size and SHA-256 checksum. A status bar message tells you when the update is ready. It installs after the last window for that copy closes.

Installed copies update in their existing installation folder. Portable copies update in place and keep their portable layout.

## Downloads

- `AnimationMaker-0.1.0-beta.5-win-x64-setup.exe` installs the app or updates an existing installation.
- `AnimationMaker-0.1.0-beta.5-win-x64-portable.zip` contains the standalone Windows app. Extract the folder and run `AnimationMaker.exe`.
- `AnimationMaker-PixiJS-Delta-0.1.0-beta.5.zip` contains the reusable PixiJS Delta runtime and TypeScript types.
- `FFmpeg-9.0.2-source.tar.gz` contains the source for the bundled FFmpeg revision.
- `SHA256SUMS.txt` contains checksums for the downloads.

The Windows packages are unsigned and require Windows x64 and Direct3D 11. Video processing runs locally.
