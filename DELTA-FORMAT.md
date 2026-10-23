# Delta Spritesheet BETA — versions 1 and 2

Temporal RGBA patches with exact PNG, bounded-error PNG and lossy WebP modes for Animation Maker and PixiJS 8.22.0. This is a custom format, not a PixiJS AnimatedSprite descriptor.

## Files and coordinates

Normal exports contain only the compact UTF-8 `delta.animation.json` manifest and `delta-000.webp` (default) or `delta-000.png` pages. Runtime/demo files and a report sidecar are not included. The app returns the quality/size report in its export summary. Pages decode to straight-alpha RGBA8 pixels. Coordinates are integer source pixels on the processed output canvas (after keying, masks, crop and maximum-height resizing). No patch rotation is used.

New exports use compact version 2. Both native and PixiJS players continue to read version 1. Update the reusable PixiJS runtime when using v2 exports; old runtime builds only understand v1.

Version 2 manifest fields:

| Field | Meaning |
|---|---|
| `format`, `version` | Exactly `animation-maker-delta`, `2` |
| `width`, `height`, `fps` | Canvas dimensions and constant frame rate |
| `pages` | Tuples `[image,width,height]`; local relative PNG or WebP names |
| `patches` | Tuples `[page,x,y,width,height]`; zero-based array indices identify patches |
| `keyframes` | Sorted frame indices marked independent; starts with 0; gaps at most 30 |
| `frames` | One flat integer array per frame. Replacement: `patchIndex,x,y`. Clear: `-1,x,y,width,height`. Concatenate operations in execution order. `[]` holds the canvas. |
| `encoding` | Optional `{mode,tolerance,textureFormat,textureQuality}`. PNG: `lossless` with 0 or `bounded-error` with 1–32; format defaults to `png`, quality is null. WebP: `lossy`, tolerance 0–32, format `webp`, quality 1–100. Missing encoding means the original lossless format. |

Every frame remains in the manifest, even when identical to its predecessor. Duration is derived from `1000/fps`, including fractional FPS. Replacement dimensions come from the referenced patch. Checkpoint flags come from `keyframes`. Frequently used patches receive the smallest numeric indices. Content hashes remain internal to deduplication and final-output verification; v2 does not ship them or repeat timing/field names on every operation.

For example, `frames: [[0,12,8],[],[-1,12,8,16,16,1,20,8]]` draws patch 0 at (12,8), holds, then clears a 16×16 rectangle and draws patch 1 at (20,8).

Version 1 uses object pages `{image,width,height}`, a patch dictionary `{id:{page,rect:{x,y,width,height}}}`, and frame objects `{durationMs,keyframe,operations,sha256}`. The 64-character hexadecimal `sha256` field is required for legacy v1 validation. It hashes reconstructed RGBA bytes, rather than the source bytes in approximate mode. Players expand v2 to the same descriptive in-memory operation model (with an empty hash); `asset.manifest` remains convenient for tools and overlays.

Operations contain `{kind,x,y,width,height,patch?}`:

- `replace`: copy all four channels from the referenced patch to this destination. Sizes must match. Transparent and semitransparent pixels overwrite the old values.
- `clear`: set all four destination channels to zero; no patch reference.

Before applying an independent frame, clear the entire canvas to RGBA zero. Apply operations in order, then display for durationMs. A non-independent frame with no operations retains the preceding canvas. Operations include any unchanged pixels within their rectangle; no change mask is necessary. An operation is not an additive color residual and must never be rendered using ordinary source-over alpha blending.

## Encoder

The app exposes **Delta tolerance** (integer 0–32, default 8) and **Preview delta · 1 second**. The preview encodes up to one second around the playhead with the same settings as a full export, opens it in the native player, and preserves the original editing session for Back to original. Preview files are local temporary assets, cleaned on return/close; preview does not replace the last full export. Classic atlas and WebM exports ignore this setting.

**Atlas image format** defaults to WebP and **WebP quality** defaults to 75/100 (medium), adjustable from 1 to 100. These settings also apply to classic atlas pages. WebP uses lossy RGB compression with preserved alpha; quality 100 is still lossy. Tolerance controls patch selection before page compression. The bounded-error guarantees below describe that stage and the final PNG output. WebP adds texture color error, so final visible error may exceed patch tolerance; its maximum/mean are measured against the exact source via the temporary PNG baseline. Use PNG with tolerance 0 for exact RGBA.

At tolerance 0, compare exact RGBA, including hidden RGB. At a positive tolerance, compare against the **previous reconstructed canvas**, not the previous source frame. A pixel changes if the alpha difference or its composited RGB difference over black or white exceeds the tolerance. Use integer premultiplied products and round the maximum error upward. Bounding both black and white also bounds every solid RGB background. Transitions from zero alpha to nonzero alpha and back always update, preventing transparent trails. RGB under zero alpha may differ in approximate mode. Every frame is checked against its source before staging; the report records maximum and mean per-pixel visible error. Tiny changes eventually trigger an update when their cumulative difference from the displayed image exceeds tolerance; they cannot drift unbounded.

Evaluate merged dirty tiles, independent reusable blocks and tightened blocks at 8/16/32/64 pixels. Also evaluate adaptive sparse partitions: greedily split at horizontal/vertical cuts that remove the most unchanged interior, with candidates at 16/32/64/128 regions. Include the changed-pixel bounding box and whole canvas as dense-motion fallbacks. Estimate incremental cost using the selected PNG/WebP quality, compact operations, patch metadata and padded texture area, reusing existing payloads globally. Cache candidate payloads within each source frame. Candidates above 128 operations are ignored. All-zero rectangles use clear. Trim zero-RGBA borders; a delta replacement clears its original rectangle before copying a smaller patch when needed, preserving disappearing pixels. Independent checkpoints omit redundant clears and also consider sparse nonzero regions and reusable blocks. Hidden RGB is retained in exact PNG mode. Periodic checkpoints remain at most 30 frames apart.

Payload identity includes width, height and SHA-256 of straight RGBA. MaxRects packs unique patches without rotation, with 1 pixel padding and no extrusion. The packer compares rectangular bin widths near the total-area square root, narrow/wide alternatives and the maximum page width, using two size orderings. It first minimises page count, then cropped page area with a small aspect-ratio penalty, reducing empty tails and long strips. Delta additionally compares the actual encoded PNG/WebP bytes of up to four dense layouts (within 25% of the best layout score) and publishes the smallest. Classic packed atlases use the geometry search; fixed grids retain their layout. The canvas plus 2 pixels must fit the configured maximum page (2048/4096/8192); oversized export gives an explicit error. Reconstruction copies pixels with nearest sampling; only the final full-canvas sprite is filtered for display.

Decode/key once. The same frame iterator feeds delta analysis and a temporary optimised packed/deduplicated classic PNG atlas. Combined compressed staging is limited to 8 GiB. Export checks decoded frame count and reconstructs every frame from the final pages. PNG hashes must match the encoder’s reconstructed canvas; WebP hashes describe the actual decoded final output and are verified in a second reconstruction pass. PNG with tolerance 0 additionally preserves every processed RGBA byte. Publish is an atomic directory move after validation; cancellation removes staging and never replaces an existing destination.

## Analysis backends and profiling

The exporter supports Auto/CPU/GPU Delta analysis independently of the export's encoding settings. The CPU implementation is the reference. The DirectCompute implementation scans RGBA8 using the same integer error arithmetic, emits packed change masks plus 16×16 tile reductions, and maintains a persistent reconstructed GPU canvas using the chosen replace/clear regions. In the normal video pipeline it reads the processor's existing final texture. Source uploads are only needed for standalone diagnostic frame sources; image codecs still require full RGBA readback into CPU memory.

Auto uses CPU below 60 frames or when frame count is unknown. Longer clips use isolated warmup, CPU/GPU equivalence checks and three timing samples. The decision includes measured transfer/synchronization cost, timing spread and initialization amortization. Unsupported or recoverably failed analysis switches back to the authoritative CPU reconstruction; keying still requires the application's GPU renderer. Cancellation is propagated rather than treated as a backend failure.

Backend selection is stored in project settings, not in the animation manifest. CPU/GPU output equivalence, including texture bytes, is regression-tested. Exact selection also checks CPU RGBA equality before publication. Hash/alpha verification remains enabled. Diagnostic reports include optional `performance` with nested/overlapping wall stages, separate Direct3D timestamp durations, compression/transfer counters, calibration time and process peak working set. Normal animation exports still contain only the manifest and image pages. Application export summaries record the same measurements in the local log.

Candidate compression is pruned only when accumulated cost already cannot beat the current winner. Final layout encodes may run concurrently with at most two workers; a conservative image-memory estimate reduces parallelism for large pages. Equal-size candidates retain original rank order, so scheduling cannot alter the selected layout.

## Playback and alpha

Native `DeltaAtlasPlayer` reconstructs straight RGBA bytes, protects its working canvas from caller mutation and serialises concurrent reads. It keeps a 256 MiB page cache. Seeking backwards or jumping 30+ frames restarts at the nearest independent frame. Cancellation invalidates incomplete state.

The PixiJS runtime decodes pages to premultiplied ImageBitmaps once and uploads them as already premultiplied. Patch sprites use `blendMode='none'`, integer positions, nearest page sampling and a persistent RGBA8 RenderTexture with resolution 1 and no MSAA/mipmaps. Clear operations draw zero RGBA with blending disabled. This preserves visible alpha semantics without repeatedly blending or premultiplying old data. A game's normal PNG path may quantise very small alpha during premultiplication; compare delta and PNG using the same GPU upload/extraction path.

Each player owns one canvas, estimated as `width*height*4` bytes. Page textures are shared by all players of one asset. Call `update(deltaMs)` before presenting the stage; intermediate patch operations are applied even if presentation skips frames. Seek and loop reset from checkpoints. Context/device restoration invalidates canvas contents and rebuilds the current frame on update; retained ImageBitmaps allow page re-upload. `invalidate()` supports external GPU resets. Destroy assets after their players or asset.destroy() destroys its remaining players.

## Runtime and offline demo

Use **PixiJS runtime ↓** in the app to save the reusable plugin ZIP independently of animation exports. It contains `delta-runtime.js` (an ES module importing the game's shared `pixi.js`), `delta-runtime.d.ts`, the IIFE build using global PIXI, package metadata, license and integration instructions. The app's separate **Player** tab previews normal exports locally, with play/pause, loop, speed, scrub, frame stepping and freeze-at-frame controls. It uses the native GPU renderer and preserves the Editor session.

Developer diagnostic exports can explicitly opt into a runtime/demo bundle. Their `demo.html` includes PixiJS in vendor/ and opens assets with a folder picker/File API; no local fetch or server is required. Older bundles remain supported. Keep each animation's JSON and texture pages together.

`DeltaAtlasAsset.load(url)`, `loadFiles(files)` and `fromManifest(manifest, resolveFile)` load assets. `asset.createPlayer(renderer)` exposes `sprite`, `texture`, `currentFrame`, `play()`, `pause()`, `gotoAndStop(index)`, `gotoAndPlay(index)`, `update(deltaMs)`, `loop`, positive `speed`, `invalidate()` and `destroy()`. Invalid manifests, out-of-bounds regions, unknown patches, unsupported versions and unsafe paths fail explicitly.

## Size report

The app's export report compares actual playable animation data. A `delta.report.json` sidecar is written only for diagnostic bundles. Classic bytes count PNG pages, atlas descriptors and animation.json; phaser.json is excluded because it is an alternative descriptor. Delta bytes count the selected PNG/WebP pages and delta.animation.json. `textureFormat` and `textureQuality` identify the selected image encoding. Estimated GPU bytes exclude driver alignment and CPU-side ImageBitmaps. A negative savingsPercent recommends classic PNG export; format selection is never silently changed.

Preview reports average/max operations, unique patches, total page pixels, atlas GPU estimate and per-player canvas memory. Changes overlay uses green for replacement and red for clear. Reports include `tolerance`, `maximumVisibleError`, `meanVisibleError` (maximum alpha/black/white channel error per pixel, averaged over all pixels/frames), and `updatedPixelPercent` (operation area divided by total canvas area across frames, including checkpoints). Savings compare approximate delta against the unchanged exact classic baseline, so they include the quality tradeoff.

## References

- [APNG frame control and SOURCE replacement](https://www.w3.org/TR/png/#11fcTL)
- [WebP animation blend/disposal and canvas assembly](https://developers.google.com/speed/webp/docs/riff_container)
- [RFC 6143: framebuffer rectangle updates](https://datatracker.ietf.org/doc/html/rfc6143)
- [Lossless Compression of Computer-Generated Animation Frames](https://www.microsoft.com/en-us/research/publication/lossless-compression-computer-generated-animation-frames/)

No code from these codecs or papers is bundled in the encoder. Motion prediction and optical flow are outside this format. Bounded-error encoding adds optional metadata; the existing replacement/clear instruction set and runtime remain compatible with older exports.
