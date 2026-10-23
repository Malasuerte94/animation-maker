# Animation Maker

Turn video into transparent animations for games and the web. Remove a green screen, refine alpha edges, crop and mask the result, then export a classic spritesheet, transparent WebM, or **beta delta animation for PixiJS**.

Windows desktop application. Local processing. GPU accelerated keying. **MIT licensed application and PixiJS runtime.**

## Download

Get the latest build from [GitHub Releases](https://github.com/Malasuerte94/animation-maker/releases).

| Download | Use |
| --- | --- |
| `AnimationMaker-…-win-x64-setup.exe` | Per-user Windows installer with a Start menu shortcut and uninstaller. |
| `AnimationMaker-…-win-x64-portable.zip` | Extract the entire folder and run `AnimationMaker.exe`. Keep the adjacent files and folders. |
| `AnimationMaker-PixiJS-Delta-…zip` | Reusable PixiJS plugin, TypeScript types and integration guide. Also available from the app. |

The Windows packages include .NET, WinUI and FFmpeg. End users do not need Node.js, Python, a development SDK, the Microsoft Store or an account. Video processing and export run locally.

**Requirements:** Windows x64, Windows 10 build 19041 or later / Windows 11, and a Direct3D 11 capable GPU. Automated GPU validation currently uses Windows 11 and an NVIDIA RTX 4070 Ti. Other hardware needs broader testing. This first beta is unsigned.

## Features

- GPU green-screen removal with Quality/Fast modes, color sampling, clean plate, despill, clip controls, choke and feather.
- Keep/Remove polygon masks with interpolated keyframes; visual crop, trim, frame rate and output-height controls.
- Packed/trimmed or grid spritesheets with PixiJS and Phaser JSON, deduplication, multiple pages and edge extrusion.
- WebP atlas quality **1–100**, default **75 (medium)**; PNG for lossless images.
- Transparent VP9 WebM with optional Opus audio.
- **Delta Animation BETA** with reusable patches and a separate PixiJS runtime.
- **Delta analysis: Auto / CPU / GPU** with DirectCompute acceleration, measured backend selection and CPU fallback.
- Independent **Player** tab, project save/load, Undo/Redo, alpha/checkerboard/photo backgrounds and zoom.

## Quick start

1. In **Editor**, open or drop an MP4, MOV or WebM.
2. Use **Auto sample green** or pick the screen color. Adjust the matte and inspect edges over white, black, magenta and **Alpha matte**. Use **Full resolution** to judge final edges.
3. Set the in/out range, frame rate and maximum frame height. Add crop or masks if needed.
4. Choose **WebP** and an image quality, or **PNG** for lossless images.
5. Export a spritesheet, transparent WebM, or **Delta Animation · BETA**.
6. Use **Play last export** or open the export folder in **Player**. Your Editor session stays available in its own tab.

Player supports play/pause, loop, 0.25–2× speed, scrub, frame stepping, backgrounds and zoom. Enter a zero-based frame in **Freeze at frame** to stop there. Space toggles playback; arrow keys step one frame. Open the animation JSON to play an atlas; a WebP page alone is a still image. Video previews are silent; WebM exports can retain audio.

## Delta Animation for PixiJS — BETA

This experimental format targets the **PixiJS 8 framework**. A small custom runtime reconstructs a persistent canvas from reusable patches. It is not a standard `AnimatedSprite` spritesheet.

### Size example

In an example reported by the project's author:

| Export | Size |
| --- | ---: |
| Normal export image | About **12 MB** |
| Delta WebP image | About **486 KB** |
| Delta animation JSON | About **1 MB** |
| Delta image + JSON | About **1.5 MB** combined |

That is roughly a **96% reduction in image size**, or **88% for the combined delta assets compared with the 12 MB image alone**. These rounded figures describe an earlier v1 result, not a general benchmark or guarantee. New exports use **compact v2 JSON** with numeric patch references and inferred frame metadata, trim empty patch borders, and search multiple atlas layouts for denser pages. Runtime bytes are separate and shared across animations.

Savings depend on content, resolution, frame count, noise, patch tolerance and WebP quality. Fully changing or noisy frames can compress less effectively. Count the complete exported assets and inspect playback quality on your own material.

### How delta works

A normal spritesheet stores complete frames even when large portions repeat. Delta keeps a reconstructed canvas and updates only regions that need to change:

1. Process source frames once, including keying, masks, crop and scaling.
2. Compare each frame with the canvas reconstructed by patch selection. Evaluate merged regions, tiles and adaptive smaller patches.
3. Store identical patches once in atlas pages and reference them by identity.
4. Record **replace** operations to overwrite RGBA, or **clear** operations to erase a rectangle. An unchanged frame has no operations.
5. Insert independent checkpoints at most 30 frames apart. Seeking and looping rebuild from a checkpoint.
6. In PixiJS, share uploaded atlas pages and apply operations to one persistent GPU render texture per player.

Transparent pixels overwrite old pixels; ordinary source-over blending would leave trails. The runtime uses replacement blending and nearest patch sampling, then displays the reconstructed canvas as a sprite.

Smaller files do not reduce GPU memory by the same ratio: each player still needs an RGBA canvas, and atlas pages consume texture memory. See [DELTA-FORMAT.md](DELTA-FORMAT.md) for the full format and checks.

### CPU/GPU analysis

Choose **Delta analysis: Auto / CPU / GPU** in the export sidebar. This setting affects Delta pixel analysis and error checks; keying/crop/resize remain GPU accelerated, and image compression stays on the CPU. GPU analysis shares the already processed Direct3D texture and keeps the reconstructed canvas on the device. CPU and GPU use equivalent integer comparisons and preserve the exported format and quality settings.

Auto keeps short or unknown-length clips on CPU to avoid calibration overhead. For at least 60 frames, it warms both backends, checks equivalence, and compares three samples including transfer/synchronization costs. It chooses GPU only when the measured gain exceeds timing variation and can repay initialization. The selected backend and fallback reason appear during export and in the summary. Project files remember the choice; older projects default to Auto. Export stage timings and GPU timestamp measurements are logged locally in `app.log`.

Independent MaxRects trials and final atlas encodes use at most two CPU workers; encoding parallelism also has an image-memory budget. Candidate searches stop once accumulated nonnegative cost proves that a candidate cannot win. These optimizations preserve deterministic selection and output bytes. Whole-export speed depends on how much time the image codecs consume; faster GPU analysis alone does not imply the same speedup for the complete export.

### Tolerance and image quality

**Delta tolerance** controls patch selection from **0 to 32**, default **8**. Positive values ignore small visible color/alpha changes such as video noise. Selection compares against the reconstructed canvas to prevent ignored changes from drifting indefinitely. Pixels appearing or disappearing are updated.

**WebP quality** independently controls lossy color compression of the final pages. It defaults to **75/100** and preserves alpha in the image codec. WebP adds color error beyond patch tolerance; quality 100 is still lossy. Use **PNG + tolerance 0** for exact processed RGBA.

Use **Preview delta · 1 second** before exporting the complete animation, then **Back to original** to restore the editing view. Export verifies final reconstruction hashes. The summary measures visible error, updated area and size against an optimized classic PNG baseline; savings can therefore include a quality tradeoff.

### Exported files and runtime

```text
my-animation/
  delta.animation.json
  delta-000.webp
  delta-001.webp        # only when another page is needed
```

PNG can be selected instead. Normal exports contain **only animation JSON and image pages**. Runtime, demo and a second PixiJS copy are not repeated in each animation.

Click **PixiJS runtime ↓** or **Player → Download PixiJS runtime…** to save a reusable ZIP. It contains `delta-runtime.js` (ES module importing your game's `pixi.js`), `delta-runtime.iife.js` (classic script using global PIXI), `delta-runtime.d.ts`, `package.json`, `README.md` and the MIT `LICENSE`. The ZIP is a separate release download too. One runtime serves all your delta animations.

### Use it in PixiJS

Use **PixiJS 8.22.0 or a compatible 8.x version**. Extract the runtime ZIP into your source tree and serve the exported JSON and images together as static assets.

```js
import { Application } from 'pixi.js';
import { DeltaAtlasAsset } from './animation-maker-pixi-delta/delta-runtime.js';

const app = new Application();
await app.init({ width: 800, height: 600, backgroundAlpha: 0 });
document.body.appendChild(app.canvas);

const asset = await DeltaAtlasAsset.load('/animations/effect/delta.animation.json');
const player = asset.createPlayer(app.renderer);
app.stage.addChild(player.sprite);
player.loop = true;
player.play();
const tick = ticker => player.update(ticker.deltaMS);
app.ticker.add(tick);

// Call these controls when needed:
// player.pause();             // freeze on the current frame
// player.gotoAndStop(12);     // freeze on frame 12 (zero-based)
// player.play();              // resume
// player.gotoAndPlay(0);      // restart
// player.speed = 0.5;         // half speed
// player.loop = false;        // stop after the last frame

// When removing the animation:
// app.ticker.remove(tick);
// app.stage.removeChild(player.sprite);
// player.destroy();
// asset.destroy();           // after all players sharing this asset are gone
```

WebGL2 and WebGPU use the same runtime. An asset can serve multiple independent players. `loadFiles(files)` supports browser folder selection; `fromManifest(manifest, resolveFile)` supports a custom Blob resolver. See the [runtime guide](runtime/pixi-delta/README.md).

### Beta status

The delta format, exporter and runtime are beta. New exports use v2; install the updated reusable PixiJS runtime for those exports. The updated app/runtime also read old v1 exports. Validate visual quality and game integration before production use. Broader hardware/browser coverage and more real-world quality testing remain planned directions, not scheduled commitments.

## Other exports and editing tools

Classic exports include `atlas-000.webp` (or `.png`), TexturePacker Hash JSON, `animation.json` for timing/order and `phaser.json` for `load.multiatlas`. Packed layout trims alpha and can reuse identical frames; grid layout keeps complete frames in row-major order. Page limits are 2048, 4096 or 8192 pixels.

WebM uses VP9 with alpha, High/Balanced/Small/Lossless presets and optional Opus audio. The lossless video preset can still involve RGB/YUV conversion; PNG is the exact image option.

**Crop canvas** supports numerical edits and drag handles. **Keep** masks protect foreground, including green details; **Remove** masks erase areas. Add keyframes by editing a mask at another time. **Image…** adds a Fill/Fit photo behind the preview without altering exports. `.animproj` stores settings and relative source references.

## Build from source

Use Windows x64, PowerShell and Node.js/npm. The project uses .NET 10 and WinUI. Open `AnimationMaker.slnx` in Visual Studio, or run:

```powershell
.\tools\bootstrap.ps1
.\tools\run.ps1
.\tools\test.ps1
.\tools\publish.ps1
.\tools\build-installer.ps1
```

Bootstrap downloads a local SDK and a version-pinned, SHA-256-verified FFmpeg build. Dependency versions are in lockfiles. Internet is needed for initial development setup. Install [Inno Setup](https://jrsoftware.org/isinfo.php) to compile the installer; `build-installer.ps1 -CompilerPath` accepts a custom compiler location.

`publish.ps1` creates a self-contained portable folder, portable ZIP, standalone PixiJS runtime ZIP and checksums. `build-installer.ps1` packages the same folder. See [CONTRIBUTING.md](CONTRIBUTING.md) for tests and release notes.

## Limits and validation

The app handles one SDR clip at a time. Complex transparency, green foreground details, compressed source video, smoke and fine motion may need masks and tuning. Export staging is limited to 8 GiB and cleaned after completion/cancellation; GPU caches are bounded. Logs are local at `%LOCALAPPDATA%/AnimationMaker/app.log`.

Tests cover GPU alpha/keying, masks, crop, WebP quality, exact PNG reconstruction, bounded patch error, delta seek/clear/checkpoints, project persistence, video decoding, WebM alpha/audio, cancellation and runtime ZIP contents. Browser tests exercise Chrome/Edge, WebGL2/WebGPU, independent players, restoration and WebP alpha. Manual visual testing remains necessary. See [VALIDATION.md](VALIDATION.md).

## License

Animation Maker's original source and PixiJS delta runtime use the [MIT License](LICENSE). Contributions are welcome under the same license.

Dependencies retain their own licenses. FFmpeg is a **separate GPLv3 executable**, not relicensed as MIT. Releases include its license, build information and source references. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
