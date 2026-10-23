# Validation — 0.1.0-beta.1

Release preparation: **2026-10-23**. Environment: Windows 11 x64, NVIDIA GeForce RTX 4070 Ti, .NET SDK 10.0.402, FFmpeg 9.0.2 essentials, Windows App SDK 2.5.1 and Vortice 3.8.3.

## Automated checks

**60 .NET tests passed.** Coverage includes GPU keying and partial alpha, masks/keyframes, alpha-aware feather/resampling, crop geometry, project persistence, H.264/HEVC decoding, 4K, MOV alpha, rotation, VFR, fractional ranges, WebM alpha/Opus, classic packed/grid atlases, multiple pages, deduplication and cancellation cleanup.

Delta/WebP checks include exact PNG RGBA reconstruction, clear/hold frames, checkpoint seeking, patch bounds, bounded patch-selection error without drift, alpha appearing/disappearing, invalid manifests/tolerance/quality, final decoded WebP hashes, measured final visible error, codec alpha preservation and quality affecting output size. Runtime ZIP tests verify contents, cancellation and preservation of existing files.

**14 headless browser tests passed in Chrome and Edge**, covering WebGL2 and WebGPU: WebP semitransparent alpha, hold/clear/seek, exact comparisons against reference PNGs using the same GPU path, loop, skipped frames, independent players, context/device restoration, the optional offline developer demo and a 1080p presentation benchmark. WebGPU correctness uses texture-buffer readback rather than canvas extraction. Browser tests do not control the desktop.

The WinUI Release build completed with no compiler errors or warnings. The Player tab and a WebP delta manifest loaded successfully in the application during development. Interactive desktop control was then stopped at the user's request; full manual interaction coverage is not claimed.

## Release packaging

- The portable ZIP includes the executable, compiled WinUI resources, .NET/WinUI/native dependencies, FFmpeg/FFprobe, the PixiJS runtime and third-party notices. Extraction was checked by comparing **617 files byte-for-byte** with the published portable folder. FFmpeg executed successfully from the extracted folder.
- The per-user Inno Setup installer compiled successfully. A silent install into a fresh workspace test directory returned exit code 0; **617 installed files** matched the portable build byte-for-byte. Silent uninstall returned exit code 0 and removed the application executable and uninstall registration. Existing installations/shortcuts were checked before the test to avoid replacing them.
- The standalone runtime ZIP contains only the reusable ES module, global-script build, TypeScript declarations, package metadata, guide and MIT license.
- The FFmpeg source archive was checked for archive integrity; its revision matches the distributor's build notice.
- Release assets have SHA-256 checksums. Builds are unsigned.

## Size and performance claims

The README's **12 MB → 486 KB texture + about 1 MB JSON** example was reported by the project's author. It is recorded as a single rounded example, not a benchmark reproduced for this release. Complete asset size, image quality and patch tolerance should be considered together. JSON and encoding optimization remain planned work.

Synthetic fixtures also exercise sparse changes and 1080p reconstruction. They are correctness/performance tests, not a guarantee of savings on real videos. Smoke, noisy footage and full-frame motion can produce different results. GPU memory is determined by atlas/canvas dimensions, not compressed file sizes.

## Suggested manual checks

1. Import your own MP4/MOV; inspect keying at full resolution over white, black, magenta and alpha matte.
2. Test crop, Keep/Remove masks, mask keyframes, trim, project save/load and Undo/Redo.
3. Export classic packed/grid, transparent WebM and beta delta assets; reopen each in Player.
4. Compare WebP quality and delta tolerance with the original, including alpha transitions and fine edges.
5. Test Player loop, pause/resume, freeze at a frame, stepping, scrub, speed and Editor/Player tab switching.
6. Save the runtime ZIP and integrate the ES module into a PixiJS game; check loading, looping, frame seeking and disposal on the target renderer/hardware.
7. Try the installer and portable build on a separate supported Windows machine without development tools installed.

Broader hardware/browser testing and visual evaluation on real source material remain necessary for this beta.
