# Contributing

Animation Maker and its PixiJS delta runtime are MIT licensed. Bug reports, test cases, documentation fixes and pull requests are welcome.

## Setup

Use Windows x64, PowerShell, Git and Node.js/npm. Run `tools/bootstrap.ps1` to fetch the local .NET SDK and pinned FFmpeg. Run `tools/build-delta-runtime.ps1` before starting the app. Open `AnimationMaker.slnx`, or use `tools/run.ps1`.

Keep personal media, logs, generated exports, `artifacts/`, `.tools/`, `bin/`, `obj/` and `node_modules/` out of commits. Use synthetic or explicitly licensed fixtures.

## Checks

```powershell
.\tools\test.ps1
.\tools\build-delta-runtime.ps1
```

The .NET tests use a real Direct3D 11 device. To generate browser-test fixtures, choose a new destination:

```powershell
.\.tools\dotnet\dotnet.exe run --project tools/AnimationMaker.DeltaDiagnostics -- artifacts/delta-validation
cd runtime/pixi-delta
npm test
```

The generator refuses to replace an existing destination. Browser tests expect fixtures in `artifacts/delta-validation` and installed Chrome/Edge. They run headless on WebGL2/WebGPU without desktop control. WebGPU comparisons read actual texture buffers.

Delta analysis diagnostics (choose new output paths):

```powershell
.\.tools\dotnet\dotnet.exe run -c Release --project tools/AnimationMaker.DeltaDiagnostics -- --profile-video artifacts/validation/demo.mp4 artifacts/profile-video-gpu Gpu 0.4
.\.tools\dotnet\dotnet.exe run -c Release --project tools/AnimationMaker.DeltaDiagnostics -- --profile artifacts/delta-validation/sparse/delta.animation.json artifacts/profile-frames-cpu Cpu 12
.\.tools\dotnet\dotnet.exe run -c Release --project tools/AnimationMaker.DeltaDiagnostics -- --benchmark-analysis artifacts/analysis-benchmark.json
```

Replace input paths with an available video or Delta fixture. The first command exercises decode, GPU processing/shared-texture analysis, codecs and verification. The second profiles an existing decoded animation (standalone GPU mode uploads its RGBA input). The benchmark runs CPU/GPU/Auto three times on small, sparse/dense 1080p and sparse 4K samples. Its medians exclude four warmup frames and measure analysis/commit/error checking, not keying or image codecs. Use Release for comparisons. Wall-stage measurements can nest or overlap; GPU timestamp durations are reported separately. Do not sum every stage as if they were mutually exclusive. `peakProcessBytes` is the process-wide high-water mark, not incremental export memory.

Read [DELTA-FORMAT.md](DELTA-FORMAT.md) before changing patch selection or playback. Preserve replacement/clear semantics, alpha transitions, independent checkpoints, old-manifest compatibility, cancellation cleanup and final-output verification. Keep C# and TypeScript validators aligned. Add focused regression tests for behavioral fixes.

## Releases

The version is in `Directory.Build.props`. Keep the standalone runtime version in sync when it changes. Run `tools/publish.ps1`, then `tools/build-installer.ps1`. Release assets and SHA-256 checksums go in `artifacts/releases/<version>/`. Keep third-party notices with the application; publish no local user media or credentials.
