# FFmpeg distribution information

Animation Maker invokes FFmpeg and FFprobe as separate, unmodified executables. Animation Maker's source and custom PixiJS runtime are MIT licensed; the FFmpeg executables and their linked dependencies retain their own licenses.

- Distributor: [Gyan Doshi / CODEX FFMPEG](https://www.gyan.dev/ffmpeg/builds/).
- Binary release: [9.0.2 essentials](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2), GPL v3-or-later.
- Original archive: `ffmpeg-9.0.2-essentials_build.zip`.
- Original archive SHA-256: `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`.
- FFmpeg source revision identified by the distributor: [`946fcce07b`](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b).
- [Source archive for that revision](https://github.com/FFmpeg/FFmpeg/archive/946fcce07b.tar.gz).
- A copy of that FFmpeg source archive accompanies the [Animation Maker release](https://github.com/Malasuerte94/animation-maker/releases) as `FFmpeg-9.0.2-source.tar.gz`.

The application package's `ffmpeg/UPSTREAM-README.txt` records the distributor's complete feature list and external library revisions. `ffmpeg/BUILD-CONFIGURATION.txt` records the actual executable configuration and binary hashes. `ffmpeg/LICENSE.txt` contains GPLv3. No modifications to the FFmpeg executables or FFmpeg source are made by this project.

The FFmpeg source archive covers FFmpeg itself. External libraries are identified separately in the distributor's README; consult their upstream sources and licenses when rebuilding or redistributing this third-party build. The distributor's release/build links above identify the original package and source provenance. [FFmpeg's legal information](https://www.ffmpeg.org/legal.html) explains its license and source-distribution requirements.
