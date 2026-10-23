# Delta Spritesheet BETA / PixiJS 8.22.0

In Animation Maker, click **PixiJS runtime ↓** or **Player → Download PixiJS runtime…** to save the reusable plugin ZIP. Extract `animation-maker-pixi-delta` into your game. Install/use your game's PixiJS 8 dependency; the plugin does not bundle a second copy. The ZIP includes ES module and global-script builds, TypeScript types, package metadata and this guide. It is shared by all your animations.

Each normal delta export contains only `delta.animation.json` and its `delta-000.webp` (or `.png`) pages. Keep those files together and serve them as game assets. Preview local exports in Animation Maker's **Player** tab. A standalone WebP page is a texture atlas; open the JSON to play its animation.

In a game, import `DeltaAtlasAsset` from `delta-runtime.js`, using PixiJS as a shared dependency:

```js
const asset = await DeltaAtlasAsset.load('/animations/effect/delta.animation.json');
const player = asset.createPlayer(app.renderer);
app.stage.addChild(player.sprite);
player.loop = true;
player.play();
const tick = ticker => player.update(ticker.deltaMS);
app.ticker.add(tick);

// Freeze on a particular frame (zero-based):
player.gotoAndStop(12);
// Resume from that point:
player.play();
// Pause on the current frame:
player.pause();
// Other controls:
player.gotoAndPlay(0);
player.speed = 0.5;
player.loop = false;

// When removing this animation:
app.ticker.remove(tick);
app.stage.removeChild(player.sprite);
player.destroy();
asset.destroy(); // after all players sharing this asset are destroyed
```

Also available: `loadFiles(files)` for a FileList and `fromManifest(manifest, resolveFile)` for a custom Blob resolver.
WebGL2 and WebGPU use the same runtime. Keep the ImageBitmap pages alive for GPU context restoration; asset.destroy() releases them.
Each player has its own RGBA8 canvas; page textures are shared. No whole-animation frame cache is created.
Call update(0) to rebuild an invalidated paused player after an external GPU reset; invalidate() can be called explicitly.
Only positive playback speed is supported. Use gotoAndStop for reverse frame stepping.
The player reconstructs a persistent canvas from reusable sparse patches. The app defaults to WebP pages at quality 75/100 (medium), adjustable from 1 to 100. WebP exports use `encoding.mode = lossy`, `textureFormat = webp`, and `textureQuality`; alpha is preserved by the image codec. Delta tolerance (0–32, default 8) controls patch selection before texture compression. WebP adds color error that can exceed that tolerance, even at quality 100. PNG exports can be exact (`lossless`, tolerance 0) or approximate (`bounded-error`, tolerance 1–32). Missing encoding metadata identifies the original exact format. In PNG mode positive tolerances bound visible error without cumulative drift. The runtime supports both `.webp` and `.png` pages, including older exports. GPU comparisons should use the same premultiplication and extraction path as ordinary sprites.

Use the app’s Delta tolerance control and Preview delta · 1 second to compare quality before a full export. The report includes tolerance, maximum/mean visible error and updated canvas percentage. The runtime applies the chosen patches as exported; it does not apply tolerance during playback.

The export summary in Animation Maker reports animation size and quality. A smaller export is not guaranteed for every clip. Older diagnostic/demo bundles with `delta.report.json` remain readable. The optional developer demo runs offline using the folder picker; it is separate from the plugin and normal exports.
PixiJS and this runtime are MIT licensed. The runtime license is included in the ZIP; PixiJS is supplied by your game.
