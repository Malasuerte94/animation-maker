import { BufferImageSource, ImageSource, Rectangle, RenderTexture, Sprite, Container, Texture } from 'pixi.js';
import type { Renderer } from 'pixi.js';

export interface DeltaRect { x: number; y: number; width: number; height: number }
export interface DeltaOperation extends DeltaRect { kind: 'replace' | 'clear'; patch?: string | null }
export interface DeltaFrame { durationMs: number; keyframe: boolean; operations: DeltaOperation[]; sha256: string }
export interface DeltaManifest {
    format: 'animation-maker-delta'; version: 1 | 2; width: number; height: number; fps: number;
    encoding?: { mode: 'lossless' | 'bounded-error' | 'lossy'; tolerance: number; textureFormat?: 'png' | 'webp'; textureQuality?: number | null } | null;
    pages: { image: string; width: number; height: number }[];
    patches: Record<string, { page: number; rect: DeltaRect }>;
    keyframes: number[]; frames: DeltaFrame[];
}
/** Compact v2 wire format; the asset exposes the expanded DeltaManifest after loading. */
export interface CompactDeltaManifest {
    format: 'animation-maker-delta'; version: 2; width: number; height: number; fps: number;
    encoding?: DeltaManifest['encoding'];
    pages: [string, number, number][];
    patches: [number, number, number, number, number][];
    keyframes: number[]; frames: number[][];
}
export type FileResolver = (relative: string) => Promise<Blob>;
const integer = (v: number, min: number, max = Number.MAX_SAFE_INTEGER) => Number.isSafeInteger(v) && v >= min && v <= max;
function expand(input: DeltaManifest | CompactDeltaManifest): DeltaManifest {
    if (!input || input.version !== 2) return input as DeltaManifest;
    // Also accept the already expanded public asset.manifest model.
    if (Array.isArray(input.frames) && input.frames.length && !Array.isArray(input.frames[0])) return input as DeltaManifest;
    const m = input as CompactDeltaManifest;
    if (!Array.isArray(m.pages) || !Array.isArray(m.patches) || !Array.isArray(m.frames) || !Array.isArray(m.keyframes)) throw Error('Invalid compact delta manifest');
    const tuple = (v: number[], length: number) => Array.isArray(v) && v.length === length && v.every(n => integer(n, 0, 2147483647));
    const pages = m.pages.map(p => {
        if (!Array.isArray(p) || p.length !== 3) throw Error('Invalid compact delta page');
        return { image: p[0], width: p[1], height: p[2] };
    });
    const patches: DeltaManifest['patches'] = Object.create(null);
    m.patches.forEach((p, i) => {
        if (!tuple(p, 5)) throw Error('Invalid compact delta patch');
        patches[String(i)] = { page: p[0], rect: { x: p[1], y: p[2], width: p[3], height: p[4] } };
    });
    const keys = new Set(m.keyframes);
    const frames = m.frames.map((values, index) => {
        if (!Array.isArray(values) || values.length > 128 * 5 || values.some(v => !integer(v, -1, 2147483647))) throw Error('Invalid compact delta frame');
        const operations: DeltaOperation[] = [];
        for (let i = 0; i < values.length;) {
            const id = values[i++], count = id === -1 ? 4 : 2;
            if (i + count > values.length || operations.length >= 128) throw Error('Invalid compact delta operation');
            const x = values[i++], y = values[i++];
            if (id === -1) operations.push({ kind: 'clear', x, y, width: values[i++], height: values[i++] });
            else {
                const patch = patches[String(id)];
                if (!patch) throw Error('Invalid compact patch reference');
                operations.push({ kind: 'replace', x, y, width: patch.rect.width, height: patch.rect.height, patch: String(id) });
            }
        }
        return { durationMs: 1000 / m.fps, keyframe: keys.has(index), operations, sha256: '' };
    });
    return { ...m, pages, patches, frames };
}
function validate(m: DeltaManifest): void {
    if (!m || m.format !== 'animation-maker-delta' || ![1, 2].includes(m.version) || !integer(m.width, 1) || !integer(m.height, 1) || m.width * m.height > 67108864 || !Number.isFinite(m.fps) || m.fps < 1 || m.fps > 240 || !Array.isArray(m.frames) || !m.frames.length || !Array.isArray(m.pages) || !Array.isArray(m.keyframes) || !m.patches) throw Error('Unsupported delta manifest');
    const inside = (r: DeltaRect, w: number, h: number) => r && integer(r.x, 0) && integer(r.y, 0) && integer(r.width, 1) && integer(r.height, 1) && r.x + r.width <= w && r.y + r.height <= h;
    if (m.encoding) {
        const e = m.encoding, format = e.textureFormat ?? 'png';
        if (!integer(e.tolerance, 0, 32) || !['png', 'webp'].includes(format) ||
            (format === 'webp' ? e.mode !== 'lossy' || !integer(e.textureQuality!, 1, 100) :
                e.mode !== (e.tolerance === 0 ? 'lossless' : 'bounded-error') || e.textureQuality != null)) throw Error('Invalid delta encoding metadata');
    }
    for (const p of m.pages) if (!p || !integer(p.width, 1, 8192) || !integer(p.height, 1, 8192) || typeof p.image !== 'string' || !/^[a-zA-Z0-9_.-]+\.(png|webp)$/.test(p.image)) throw Error('Invalid delta page');
    for (const p of Object.values(m.patches)) if (!p || !integer(p.page, 0, m.pages.length - 1) || !inside(p.rect, m.pages[p.page].width, m.pages[p.page].height)) throw Error('Invalid delta patch');
    const keys: number[] = []; let last = -30;
    m.frames.forEach((f, i) => {
        if (!f || !Number.isFinite(f.durationMs) || Math.abs(f.durationMs - 1000 / m.fps) > .000001 || !Array.isArray(f.operations) || f.operations.length > 128 || typeof f.keyframe !== 'boolean' || (m.version === 1 && !/^[0-9a-fA-F]{64}$/.test(f.sha256))) throw Error('Invalid delta frame');
        if (f.keyframe) { keys.push(i); last = i; }
        if (i - last >= 30) throw Error('Missing delta checkpoint');
        for (const o of f.operations) {
            if (!inside(o, m.width, m.height)) throw Error('Delta operation outside canvas');
            if (o.kind === 'clear') { if (o.patch != null) throw Error('Invalid clear operation'); }
            else if (o.kind !== 'replace' || !o.patch || !Object.hasOwn(m.patches, o.patch) || m.patches[o.patch].rect.width !== o.width || m.patches[o.patch].rect.height !== o.height) throw Error('Invalid replacement operation');
        }
    });
    if (!keys.length || keys[0] !== 0 || keys.length !== m.keyframes.length || keys.some((k, i) => k !== m.keyframes[i])) throw Error('Invalid checkpoint index');
}

/** Immutable page textures shared by players. Destroy players before destroying their asset. */
export class DeltaAtlasAsset {
    readonly manifest: DeltaManifest;
    readonly patches = new Map<string, Texture>();
    readonly pages: Texture[] = [];
    readonly clearTexture: Texture;
    private players = new Set<DeltaPlayer>();
    private bitmaps: ImageBitmap[] = [];
    private destroyed = false;
    private constructor(manifest: DeltaManifest) {
        validate(manifest); this.manifest = manifest;
        this.clearTexture = new Texture({ source: new BufferImageSource({ resource: new Uint8Array(4), width: 1, height: 1, format: 'rgba8unorm', alphaMode: 'premultiplied-alpha', scaleMode: 'nearest' }) });
    }
    static async load(url: string | URL): Promise<DeltaAtlasAsset> {
        const manifestUrl = new URL(url, globalThis.location?.href);
        const fetchBlob = async (u: URL) => { const r = await fetch(u); if (!r.ok) throw Error(`Failed to load ${u}: ${r.status}`); return r.blob(); };
        const manifest = JSON.parse(await (await fetchBlob(manifestUrl)).text());
        return DeltaAtlasAsset.fromManifest(manifest, name => {
            const page = new URL(name, manifestUrl);
            if (page.origin !== manifestUrl.origin || !page.pathname.startsWith(new URL('.', manifestUrl).pathname)) throw Error('Page URL outside animation directory');
            return fetchBlob(page);
        });
    }
    static async loadFiles(files: Iterable<File>): Promise<DeltaAtlasAsset> {
        const all = Array.from(files), manifest = all.find(f => f.name === 'delta.animation.json');
        if (!manifest) throw Error('Select a folder containing delta.animation.json');
        const prefix = manifest.webkitRelativePath ? manifest.webkitRelativePath.slice(0, -manifest.name.length) : '';
        return DeltaAtlasAsset.fromManifest(JSON.parse(await manifest.text()), async name => {
            const file = all.find(f => prefix ? f.webkitRelativePath === prefix + name : f.name === name);
            if (!file) throw Error(`Missing atlas page: ${name}`); return file;
        });
    }
    static async fromManifest(input: DeltaManifest | CompactDeltaManifest, resolveFile: FileResolver): Promise<DeltaAtlasAsset> {
        const manifest = expand(input);
        const asset = new DeltaAtlasAsset(manifest);
        try {
            for (const p of manifest.pages) {
                // Premultiply once while decoding, then upload without a second conversion on either backend.
                const bitmap = await createImageBitmap(await resolveFile(p.image), { premultiplyAlpha: 'premultiply', colorSpaceConversion: 'none' });
                asset.bitmaps.push(bitmap);
                if (bitmap.width !== p.width || bitmap.height !== p.height) throw Error('Atlas image dimensions differ from manifest');
                asset.pages.push(new Texture({ source: new ImageSource({ resource: bitmap, resolution: 1, alphaMode: 'premultiplied-alpha', scaleMode: 'nearest', autoGenerateMipmaps: false }) }));
            }
            for (const [id, p] of Object.entries(manifest.patches)) asset.patches.set(id, new Texture({ source: asset.pages[p.page].source, frame: new Rectangle(p.rect.x, p.rect.y, p.rect.width, p.rect.height) }));
            return asset;
        } catch (e) { asset.destroy(); throw e; }
    }
    createPlayer(renderer: Renderer): DeltaPlayer {
        if (this.destroyed) throw Error('Delta asset is destroyed');
        const player = new DeltaPlayer(this, renderer, p => this.players.delete(p)); this.players.add(player); return player;
    }
    destroy(): void {
        if (this.destroyed) return; this.destroyed = true;
        for (const player of Array.from(this.players)) player.destroy();
        for (const p of this.patches.values()) p.destroy(false);
        for (const p of this.pages) p.destroy(true);
        this.clearTexture.destroy(true); for (const b of this.bitmaps) b.close();
        this.patches.clear(); this.pages.length = 0;
    }
}

export class DeltaPlayer {
    readonly sprite: Sprite;
    readonly texture: RenderTexture;
    loop = true;
    speed = 1;
    playing = false;
    currentFrame = -1;
    private positionMs = 0;
    private destroyed = false;
    private dirty = false;
    private suspended = false;
    private readonly stage = new Container();
    private readonly pool: Sprite[] = [];
    private restore = () => { this.suspended = false; this.invalidate(); };
    private loss = () => { this.suspended = true; this.dirty = true; };
    private watchedDevice: unknown = null;
    private contextObserver = { contextChange: (context: unknown) => { this.observeDevice(context); this.restore(); } };
    private observeDevice(context: unknown): void {
        const device = (context as { device?: { lost: Promise<unknown> } } | null)?.device;
        this.watchedDevice = device;
        if (device) void device.lost.then(() => { if (!this.destroyed && this.watchedDevice === device) this.loss(); });
    }
    constructor(readonly asset: DeltaAtlasAsset, readonly renderer: Renderer, private released: (player: DeltaPlayer) => void) {
        const m = asset.manifest;
        this.texture = RenderTexture.create({ width: m.width, height: m.height, resolution: 1, format: 'rgba8unorm', alphaMode: 'premultiplied-alpha', antialias: false, autoGenerateMipmaps: false, scaleMode: 'linear' });
        this.sprite = new Sprite(this.texture);
        renderer.canvas.addEventListener('webglcontextlost', this.loss);
        renderer.canvas.addEventListener('webglcontextrestored', this.restore);
        renderer.runners.contextChange?.add(this.contextObserver);
        this.observeDevice((renderer as unknown as { gpu?: unknown }).gpu);
        this.gotoAndStop(0);
    }
    /** Mark canvas contents for rebuilding after an external renderer/device reset. */
    invalidate(): void { this.dirty = true; }
    play(): void { this.playing = true; }
    pause(): void { this.playing = false; }
    gotoAndStop(frame: number): void {
        this.playing = false; const f = this.clamp(frame); this.positionMs = f * 1000 / this.asset.manifest.fps; this.seek(f);
    }
    gotoAndPlay(frame: number): void { this.gotoAndStop(frame); this.play(); }
    private clamp(frame: number): number {
        if (!Number.isFinite(frame)) throw Error('Frame must be finite');
        return Math.max(0, Math.min(this.asset.manifest.frames.length - 1, Math.trunc(frame)));
    }
    update(deltaMs: number): void {
        if (this.destroyed || this.suspended) return;
        if (!Number.isFinite(deltaMs) || deltaMs < 0 || !Number.isFinite(this.speed) || this.speed <= 0) throw Error('Time must be nonnegative and speed positive');
        const m = this.asset.manifest, duration = m.frames.length * 1000 / m.fps;
        let wrapped = false;
        if (this.playing) {
            this.positionMs += deltaMs * this.speed;
            if (this.positionMs >= duration) {
                if (this.loop) { this.positionMs %= duration; wrapped = true; }
                else { this.positionMs = duration; this.playing = false; }
            }
        }
        const target = Math.min(m.frames.length - 1, Math.floor(this.positionMs * m.fps / 1000 + 1e-8));
        if (this.dirty || wrapped || target < this.currentFrame || target - this.currentFrame >= 30) this.seek(target);
        else for (let i = this.currentFrame + 1; i <= target; i++) this.apply(i);
    }
    private seek(target: number): void {
        if (this.destroyed) throw Error('Delta player is destroyed');
        if (this.suspended) { this.dirty = true; return; }
        let start = 0; for (const k of this.asset.manifest.keyframes) { if (k > target) break; start = k; }
        for (let i = start; i <= target; i++) this.apply(i);
        this.dirty = false;
    }
    private apply(index: number): void {
        const f = this.asset.manifest.frames[index];
        for (let i = 0; i < Math.max(f.operations.length, this.pool.length); i++) {
            const op = f.operations[i]; let sprite = this.pool[i];
            if (!sprite && op) { sprite = new Sprite(); sprite.blendMode = 'none'; this.pool.push(sprite); this.stage.addChild(sprite); }
            if (!sprite) continue;
            sprite.visible = !!op;
            if (op) {
                sprite.texture = op.kind === 'clear' ? this.asset.clearTexture : this.asset.patches.get(op.patch!)!;
                sprite.position.set(op.x, op.y); sprite.width = op.width; sprite.height = op.height;
            }
        }
        if (f.keyframe || f.operations.length) this.renderer.render({ container: this.stage, target: this.texture, clear: f.keyframe, clearColor: [0, 0, 0, 0] });
        this.currentFrame = index;
    }
    destroy(): void {
        if (this.destroyed) return; this.destroyed = true; this.playing = false;
        this.renderer.canvas.removeEventListener('webglcontextlost', this.loss);
        this.renderer.canvas.removeEventListener('webglcontextrestored', this.restore);
        this.renderer.runners.contextChange?.remove(this.contextObserver);
        this.watchedDevice = null;
        this.sprite.destroy(); this.stage.destroy({ children: true }); this.texture.destroy(true); this.released(this);
    }
}
