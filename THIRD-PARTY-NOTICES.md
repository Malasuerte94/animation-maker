# Third-party components

The MIT license at the repository root covers Animation Maker's original source.
Bundled runtimes and dependencies retain their own licenses and notices.

| Component | Upstream / license information |
| --- | --- |
| .NET runtime | [dotnet/runtime](https://github.com/dotnet/runtime), MIT; distribution notices accompany the runtime. |
| Windows App SDK / WinUI | [WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK), [WinUI](https://github.com/microsoft/microsoft-ui-xaml); consult their Microsoft redistributable and third-party notices. |
| Vortice.Windows / SharpGen.Runtime | [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows), [SharpGenTools](https://github.com/SharpGenTools/SharpGenTools), MIT. |
| SkiaSharp / Skia | [SkiaSharp](https://github.com/mono/SkiaSharp), MIT; [Skia](https://skia.googlesource.com/skia/), BSD-style and third-party notices. |
| FFmpeg, libvpx, libopus and other enabled codecs | [FFmpeg](https://ffmpeg.org), [Gyan builds](https://www.gyan.dev/ffmpeg/builds/). The bundled essentials build is GPL v3-or-later. Its GPL text is in `ffmpeg/LICENSE.txt`; complete build configuration is available with `ffmpeg/ffmpeg.exe -version`. Corresponding source/build information is provided by the build distributor and [FFmpeg's release archives](https://ffmpeg.org/releases/). Redistributing this binary requires satisfying its source and notice obligations. |
| xUnit and Microsoft.NET.Test.Sdk | Development/test-only dependencies; not required by the player. |
| PixiJS 8.22.0 | [pixijs/pixijs](https://github.com/pixijs/pixijs/tree/v8.22.0), MIT. Bundled only for the offline delta demo; its license is in `delta-runtime/vendor/PIXI-LICENSE.txt`. Game integration shares the application's PixiJS dependency. |
| TypeScript, esbuild and Playwright | Development/test-only dependencies used to build and validate the delta runtime; not required by the portable application or exported demo. Locked versions are recorded in `runtime/pixi-delta/package-lock.json`. |
| Inno Setup | Installer compiler and setup engine by Jordan Russell and Martijn Laan; [license and redistribution terms](https://github.com/jrsoftware/issrc/blob/main/license.txt). Needed only to build the installer, not to run the application. |

The installer and portable archive include collected dependency license/notice files in `licenses/`, package version metadata and the original FFmpeg license. The FFmpeg version is pinned to the unmodified Gyan 9.0.2 essentials build; its original archive is verified by SHA-256. See [FFmpeg source/build information](third-party/ffmpeg/SOURCE.md), the packaged `ffmpeg/UPSTREAM-README.txt` and `ffmpeg/BUILD-CONFIGURATION.txt`. A matching FFmpeg source archive is offered alongside the release downloads. The app invokes FFmpeg/FFprobe as separate processes.

The GPU keyer is an original implementation of color-difference matting,
screen subtraction and chroma-distance keying. Algorithm reference:
[Natron PIK documentation](https://natron.readthedocs.io/en/v2.3.15/plugins/net.sf.openfx.PIK.html).
No Natron or OBS source code is bundled in the keyer.

No AI models, hosted processing APIs, analytics or upload services are used.
