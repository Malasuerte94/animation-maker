# Validation — 0.1.0-beta.2

Release preparation: **2026-10-23**. Environment: Windows 11 x64, NVIDIA GeForce RTX 4070 Ti, .NET SDK 10.0.402, FFmpeg 9.0.2 essentials, Windows App SDK 2.5.1 and Vortice 3.8.3.

## Automated checks

**83 .NET tests passed in Release.** Coverage includes GPU keying and partial alpha, masks/keyframes, alpha-aware feather/resampling, crop geometry, project persistence, H.264/HEVC decoding, 4K, MOV alpha, rotation, VFR, fractional ranges, WebM alpha/Opus, classic packed/grid atlases, multiple pages, deduplication and cancellation cleanup. Export checks cover compact v2 size/tuples, v1 compatibility, malformed compact input, transparent-border trimming including hidden RGB, sparse reconstruction without trails, and compact classic packing.

Delta/WebP checks include exact PNG RGBA reconstruction, clear/hold frames, checkpoint seeking, patch bounds, bounded patch-selection error without drift, alpha appearing/disappearing, invalid manifests/tolerance/quality, final decoded WebP hashes, measured final visible error, codec alpha preservation and quality affecting output size. Runtime ZIP tests verify contents, cancellation and preservation of existing files.

**18 headless browser tests passed in Chrome and Edge**, covering WebGL2 and WebGPU: compact v2 tuples and malformed-input rejection before image loading, legacy v1 WebP semitransparent alpha, hold/clear/seek, exact comparisons against v2 reference PNGs using the same GPU path, loop, skipped frames, independent players, context/device restoration, the optional offline developer demo and a 1080p presentation benchmark. WebGPU correctness uses texture-buffer readback rather than canvas extraction. Browser tests do not control the desktop. Use `DELTA_FIXTURES=/artifacts/delta-validation-compact` for the new generated v2 fixtures; the original v1 fixture directory remains available.

The WinUI Release build completed with no compiler errors or warnings. The Player tab and a WebP delta manifest loaded successfully in the application during development. Interactive desktop control was then stopped at the user's request; full manual interaction coverage is not claimed.

## Delta CPU/GPU integration

The DirectCompute backend was validated on the RTX 4070 Ti. Regression tests compare CPU/GPU change masks, dirty tiles, nonzero/changed bounds and integer error sums on odd dimensions, partial-alpha/hidden-RGB samples and 1080p frames. Tests cover persistent reconstruction, skipped updates, keyframe clears, shared processor textures without RGBA re-upload, project-setting persistence/defaults, short-clip Auto selection, calibration mismatch rejection, forced-GPU fallback during initialization/commit, cancellation and cleanup during parallel atlas encoding. End-to-end CPU/GPU PNG/WebP exports have byte-identical manifests and texture pages. A real FFmpeg/service test verifies GPU selection, hardware timestamps and shared-texture counters.

A Release benchmark uses 64 processed frames per case, three independent runs per backend, and excludes the first four frames from the median. The table reports the median of the three per-run medians for scan + commit + CPU mirror update + error check. It excludes keying, codec work and initialization/calibration; those costs are reported separately in the benchmark JSON and included in Auto's amortization decision.

| Workload | CPU | Forced GPU | Auto result |
|---|---:|---:|---|
| 277 × 512 sparse | 1.23 ms | 0.17 ms | CPU |
| 1920 × 1080 sparse | 21.57 ms | 0.89 ms | GPU |
| 1920 × 1080 dense | 28.52 ms | 4.01 ms | GPU |
| 3840 × 2160 sparse | 75.40 ms | 2.19 ms | GPU |

These are analysis timings, not complete-export speedups. On a 12-frame synthetic video, candidate WebP probe calls fell from 4,812 to 3,716 through exact-cost pruning, with identical output bytes. The processed-frame GPU path recorded zero source RGBA re-uploads into the analyzer. RGBA readback took about 14 ms of a roughly 4.8-second full-export sample, so asynchronous readback and a staging ring were deferred. SIMD scan work was also deferred after the GPU backend addressed the measured scan cost. MaxRects layout trials and finalist encodes now run on at most two CPU workers with deterministic rank/tie handling; encoding parallelism is limited by a conservative image-memory budget.

The 98-frame local fixture was re-exported with forced GPU analysis and all output hashes verified. Its manifest and WebP page are byte-identical to the earlier optimized CPU export (1,032,665 combined bytes). Local evidence is in `artifacts/gpu-analysis-benchmark.json`, `artifacts/gpu-plan-video-cpu`, `artifacts/gpu-plan-video-gpu`, `artifacts/gpu-plan-video-pruned` and `artifacts/gpu-plan-real-parallel`. Wall stages may nest/overlap; GPU times come from Direct3D timestamp queries. Process peak memory is a process-wide high-water mark. Other GPU vendors need physical validation; recoverable backend failures are covered by simulated fallback tests.

## Release packaging

These packaging checks apply to the beta.2 release packages.

- The portable ZIP includes the executable, compiled WinUI resources, .NET/WinUI/native dependencies, FFmpeg/FFprobe, the PixiJS runtime and third-party notices. Extraction was checked by comparing **617 files byte-for-byte** with the published portable folder. FFmpeg executed successfully from the extracted folder.
- The per-user Inno Setup installer compiled successfully. A silent install into a fresh workspace test directory returned exit code 0; **617 installed files** matched the portable build byte-for-byte. Silent uninstall returned exit code 0 and removed the application executable and uninstall registration. Existing installations/shortcuts were checked before the test to avoid replacing them.
- The standalone runtime ZIP contains only the reusable ES module, global-script build, TypeScript declarations, package metadata, guide and MIT license.
- The FFmpeg source archive was checked for archive integrity; its revision matches the distributor's build notice.
- Release assets have SHA-256 checksums. Builds are unsigned.

## Size and performance claims

The README's **12 MB → 486 KB texture + about 1 MB JSON** example was reported by the project's author. It is recorded as a single rounded v1 example, not a benchmark reproduced for this release. Complete asset size, image quality and patch tolerance should be considered together. Compact v2 JSON and denser packing are now implemented; per-export savings still depend on content.

A separate 98-frame local comparison re-exported the same processed RGBA source at WebP quality 75 and tolerance 8:

| Playable assets | Previous exporter | Optimized exporter |
|---|---:|---:|
| Delta JSON | 1,195,701 bytes | 93,237 bytes |
| WebP pages | 919,172 bytes | 939,428 bytes |
| Combined | 2,114,873 bytes | 1,032,665 bytes |
| Atlas dimensions | 1149 × 4096 | 1680 × 1971 |

JSON decreased 92.2%, combined bytes 51.2%, and texture area 29.6%. Image bytes increased 2.2% on this clip: transparent space compresses well, and changed patch boundaries/layout affect lossy coding. Both exports used the same quality/tolerance; final reconstruction and alpha checks passed on all 98 frames. More candidate comparisons also increase export work. These figures describe this local fixture, not a guarantee for every clip. The untracked local evidence is `artifacts/delta-optimization-comparison.json`.

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
