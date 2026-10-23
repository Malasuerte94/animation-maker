using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AnimationMaker.Core;

public sealed record DeltaPage(string Image, int Width, int Height);
public sealed record DeltaPatch(int Page, RectI Rect);
public sealed record DeltaOperation(string Kind, int X, int Y, int Width, int Height, string? Patch = null);
public sealed record DeltaFrame(double DurationMs, bool Keyframe, List<DeltaOperation> Operations, string Sha256);
public sealed record DeltaEncoding(string Mode, int Tolerance, string TextureFormat = "png", int? TextureQuality = null);
public sealed record DeltaManifest(string Format, int Version, int Width, int Height, double Fps,
    List<DeltaPage> Pages, Dictionary<string, DeltaPatch> Patches, List<int> Keyframes, List<DeltaFrame> Frames,
    DeltaEncoding? Encoding = null);
public sealed record DeltaReport(long ClassicBytes, long DeltaBytes, double SavingsPercent, int Pages, long AtlasPixels,
    int UniquePatches, double AverageOperations, int MaximumOperations, long AtlasGpuBytes, long PerPlayerGpuBytes,
    int VerifiedFrames, long RuntimeBytes, long DemoBytes, string Recommendation,
    int Tolerance = 0, int MaximumVisibleError = 0, double MeanVisibleError = 0, double UpdatedPixelPercent = 0,
    string TextureFormat = "png", int? TextureQuality = null);

public static class DeltaAtlasExporter
{
    public const string Format = "animation-maker-delta";
    public const int KeyframeInterval = 30, MaximumOperations = 128;
    private const long StagingLimit = 8L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions CompactJson = new(ProjectStore.JsonOptions) { WriteIndented = false };
    private sealed record Payload(string Id, RgbaFrame Image, byte[] Png);
    private sealed record Candidate(List<DeltaOperation> Operations, Dictionary<string, Payload> NewPayloads, long Cost);

    public static async Task<DeltaReport> ExportAsync(IAsyncEnumerable<RgbaFrame> frames, string destination,
        ExportSettings settings, double fps, IProgress<ExportProgress>? progress = null, int expectedFrames = 0,
        CancellationToken ct = default, string? runtimeDirectory = null)
    {
        if (!double.IsFinite(fps) || fps <= 0 || fps > 240) throw new InvalidDataException("Invalid delta FPS.");
        if (settings.MaxAtlasSize is not (2048 or 4096 or 8192)) throw new InvalidDataException("Invalid delta atlas size.");
        if (settings.DeltaTolerance is < 0 or > 32) throw new InvalidDataException("Delta tolerance must be between 0 and 32.");
        settings.ValidateImageEncoding();
        bool lossyTextures = settings.ImageFormat == AtlasImageFormat.Webp;
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Choose a new output folder for the delta atlas.");
        string staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        string cache = Path.Combine(staging, ".patches"), baseline = Path.Combine(staging, ".classic");
        Directory.CreateDirectory(cache);
        var stored = new Dictionary<string, SpriteRecord>();
        var encodedFrames = new List<DeltaFrame>(); var keys = new List<int>();
        long payloadBytes = 0, baselineBytes = 0; int width = 0, height = 0, lastKey = -KeyframeInterval;
        long errorSum = 0, updatedPixels = 0; int maximumError = 0;
        void checkBudget() { if (payloadBytes + baselineBytes > StagingLimit) throw new IOException("Delta staging exceeds 8 GiB. Export a shorter interval or lower frame height."); }
        try
        {
            async IAsyncEnumerable<RgbaFrame> encode([EnumeratorCancellation] CancellationToken token = default)
            {
                RgbaFrame? reconstructed = null;
                await foreach (var frame in frames.WithCancellation(token))
                {
                    token.ThrowIfCancellationRequested(); frame.Validate();
                    if (reconstructed == null) { width = frame.Width; height = frame.Height; reconstructed = new(width, height, new byte[frame.Pixels.Length]); }
                    if (frame.Width != width || frame.Height != height) throw new InvalidDataException("Delta frames must have equal canvas dimensions.");
                    if (width + 2 > settings.MaxAtlasSize || height + 2 > settings.MaxAtlasSize) throw new InvalidDataException("The delta canvas exceeds the atlas page. Lower frame height or increase atlas size.");
                    int index = encodedFrames.Count;
                    bool keyframe = index - lastKey >= KeyframeInterval;
                    Candidate best;
                    if (keyframe) best = Independent(frame, stored, token);
                    else
                    {
                        // Compare against what the player actually displays, never against the previous source.
                        // This bounds approximation error even after long runs of tiny changes.
                        var changes = Changes(reconstructed, frame, settings.DeltaTolerance, token);
                        if (changes.Bounds == null) best = new([], [], 0);
                        else
                        {
                            best = Evaluate(frame, [changes.Bounds.Value], stored, token);
                            foreach (int tile in new[] { 8, 16, 32, 64 })
                            {
                                var rects = TileRectangles(changes.Mask, width, height, tile, token);
                                Consider(rects);
                                // Fixed blocks preserve content identities across frames and checkpoints.
                                // Tight blocks avoid shipping the unchanged interior around sparse motion.
                                var blocks = Blocks(changes.Mask, width, height, tile, token);
                                Consider(blocks);
                                Consider(blocks.Select(r => Tighten(r, changes.Mask, width)).ToList());
                            }
                            foreach (var partition in SparsePartitions(changes.Mask, width, changes.Bounds.Value, token)) Consider(partition);
                            var full = Evaluate(frame, [new(0, 0, width, height)], stored, token);
                            if (full.Cost <= best.Cost) { best = full; keyframe = true; }
                            if (best.Operations is [{ X: 0, Y: 0 } op] && op.Width == width && op.Height == height) keyframe = true;
                            void Consider(List<RectI> rects)
                            {
                                if (rects.Count > MaximumOperations) return;
                                var candidate = Evaluate(frame, rects, stored, token);
                                if (candidate.Cost < best.Cost) best = candidate;
                            }
                        }
                    }
                    foreach (var payload in best.NewPayloads.Values)
                    {
                        string file = Path.Combine(cache, payload.Id + ".png");
                        await File.WriteAllBytesAsync(file, payload.Png, token); payloadBytes += payload.Png.Length; checkBudget();
                        stored.Add(payload.Id, new(payload.Id, file, new(0, 0, payload.Image.Width, payload.Image.Height), new(width, height), payload.Id));
                    }
                    if (keyframe) { lastKey = index; keys.Add(index); }
                    if (keyframe) Array.Clear(reconstructed.Pixels);
                    foreach (var op in best.Operations)
                    {
                        updatedPixels += (long)op.Width * op.Height;
                        for (int y = op.Y; y < op.Y + op.Height; y++)
                        {
                            int offset = (y * width + op.X) * 4;
                            Buffer.BlockCopy(frame.Pixels, offset, reconstructed.Pixels, offset, op.Width * 4);
                        }
                    }
                    for (int i = 0; i < frame.Pixels.Length; i += 4)
                    {
                        int error = VisibleError(frame.Pixels, reconstructed.Pixels, i);
                        maximumError = Math.Max(maximumError, error); errorSum += error;
                        if (error > settings.DeltaTolerance) throw new InvalidDataException("Delta error exceeds the selected tolerance.");
                    }
                    encodedFrames.Add(new(1000 / fps, keyframe, best.Operations, Convert.ToHexString(SHA256.HashData(reconstructed.Pixels))));
                    progress?.Report(new(settings.DeltaTolerance == 0 ? "Delta · exact RGBA patches" : $"Delta · sparse patches · tolerance {settings.DeltaTolerance}/255", index + 1, expectedFrames));
                    yield return frame;
                }
            }
            // The classic encoder pulls this iterator: decode/keying runs once, and both encoders see the exact same bytes.
            // Keep an exact classic baseline to measure final WebP error without decoding/keying the video again.
            await AtlasExporter.ExportAsync(encode(ct), baseline, settings with { Layout = AtlasLayout.Packed, Deduplicate = true, ImageFormat = AtlasImageFormat.Png }, fps,
                expectedFrames: expectedFrames, ct: ct, stagedBytesChanged: bytes => { baselineBytes = bytes; checkBudget(); });
            baselineBytes = Directory.EnumerateFiles(baseline).Where(f => !f.EndsWith("phaser.json", StringComparison.Ordinal)).Sum(f => new FileInfo(f).Length);
            checkBudget();
            var pages = new List<AtlasPage>(); var patches = new Dictionary<string, DeltaPatch>();
            foreach (var sprite in stored.Values.OrderByDescending(s => Math.Max(s.Bounds.Width, s.Bounds.Height)).ThenBy(s => s.Name, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested(); int w = sprite.Bounds.Width + 2, h = sprite.Bounds.Height + 2;
                int pageIndex = pages.FindIndex(p => p.TryPlace(sprite, w, h));
                if (pageIndex < 0) { var page = new AtlasPage(settings.MaxAtlasSize); if (!page.TryPlace(sprite, w, h)) throw new InvalidDataException("Patch does not fit in the atlas."); pages.Add(page); pageIndex = pages.Count - 1; }
                var slot = pages[pageIndex].Sprites[^1].Slot;
                patches.Add(sprite.Name, new(pageIndex, new(slot.X + 1, slot.Y + 1, sprite.Bounds.Width, sprite.Bounds.Height)));
            }
            var pageData = new List<DeltaPage>();
            for (int i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested(); var page = pages[i];
                var image = new RgbaFrame(page.Width, page.Height, new byte[checked(page.Width * page.Height * 4)]);
                foreach (var placement in page.Sprites)
                {
                    ct.ThrowIfCancellationRequested(); var patch = Imaging.Load(placement.Sprite.File);
                    for (int y = 0; y < patch.Height; y++) Buffer.BlockCopy(patch.Pixels, y * patch.Width * 4, image.Pixels,
                        ((placement.Slot.Y + 1 + y) * image.Width + placement.Slot.X + 1) * 4, patch.Width * 4);
                }
                string name = $"delta-{i:D3}" + settings.ImageExtension; Imaging.SaveAtlasImage(Path.Combine(staging, name), image, settings);
                payloadBytes += new FileInfo(Path.Combine(staging, name)).Length; checkBudget();
                pageData.Add(new(name, page.Width, page.Height)); progress?.Report(new("Packing delta pages", i + 1, pages.Count));
            }
            var manifest = new DeltaManifest(Format, 1, width, height, fps, pageData, patches, keys, encodedFrames,
                new(lossyTextures ? "lossy" : settings.DeltaTolerance == 0 ? "lossless" : "bounded-error", settings.DeltaTolerance,
                    lossyTextures ? "webp" : "png", lossyTextures ? settings.WebpQuality : null));
            string manifestPath = Path.Combine(staging, "delta.animation.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, CompactJson), ct);
            var player = new DeltaAtlasPlayer(manifestPath);
            var sourcePlayer = lossyTextures ? new AtlasPlayer(Path.Combine(baseline, "animation.json")) : null;
            if (lossyTextures) { maximumError = 0; errorSum = 0; }
            for (int i = 0; i < encodedFrames.Count; i++)
            {
                ct.ThrowIfCancellationRequested(); var reconstructed = player.ReadFrame(i, ct);
                string hash = Convert.ToHexString(SHA256.HashData(reconstructed.Pixels));
                if (sourcePlayer != null)
                {
                    var source = sourcePlayer.ReadFrame(i, ct);
                    for (int offset = 0; offset < source.Pixels.Length; offset += 4)
                    {
                        int error = VisibleError(source.Pixels, reconstructed.Pixels, offset);
                        maximumError = Math.Max(maximumError, error); errorSum += error;
                        if (source.Pixels[offset + 3] != reconstructed.Pixels[offset + 3] &&
                            Math.Abs(source.Pixels[offset + 3] - reconstructed.Pixels[offset + 3]) > settings.DeltaTolerance)
                            throw new InvalidDataException($"WebP alpha verification failed at frame {i}.");
                    }
                    // Hash the decoded published texture bytes, including the selected lossy compression.
                    encodedFrames[i] = encodedFrames[i] with { Sha256 = hash };
                }
                else if (hash != encodedFrames[i].Sha256) throw new InvalidDataException($"Delta verification failed at frame {i}.");
                progress?.Report(new("Verifying runtime reconstruction hashes", i + 1, encodedFrames.Count));
            }
            if (lossyTextures)
            {
                await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, CompactJson), ct);
                var verified = new DeltaAtlasPlayer(manifestPath);
                for (int i = 0; i < encodedFrames.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Convert.ToHexString(SHA256.HashData(verified.ReadFrame(i, ct).Pixels)) != encodedFrames[i].Sha256)
                        throw new InvalidDataException($"WebP delta verification failed at frame {i}.");
                    progress?.Report(new("Verifying published WebP frames", i + 1, encodedFrames.Count));
                }
            }
            long dataBytes = pageData.Sum(p => new FileInfo(Path.Combine(staging, p.Image)).Length) + new FileInfo(manifestPath).Length;
            long runtimeBytes = 0, demoBytes = 0;
            if (runtimeDirectory != null)
            {
                if (!File.Exists(Path.Combine(runtimeDirectory, "demo.html")) || !File.Exists(Path.Combine(runtimeDirectory, "delta-runtime.iife.js"))) throw new FileNotFoundException("The delta runtime is missing. Build runtime/pixi-delta before publishing.");
                foreach (var file in Directory.EnumerateFiles(runtimeDirectory, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested(); string relative = Path.GetRelativePath(runtimeDirectory, file); string target = Path.Combine(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
                    if (relative.StartsWith("vendor", StringComparison.Ordinal) || relative == "demo.html") demoBytes += new FileInfo(file).Length; else runtimeBytes += new FileInfo(file).Length;
                }
            }
            double savings = baselineBytes == 0 ? 0 : (1 - (double)dataBytes / baselineBytes) * 100;
            var report = new DeltaReport(baselineBytes, dataBytes, savings, pages.Count, pageData.Sum(p => (long)p.Width * p.Height),
                patches.Count, encodedFrames.Average(f => f.Operations.Count), encodedFrames.Max(f => f.Operations.Count),
                pageData.Sum(p => (long)p.Width * p.Height * 4), (long)width * height * 4, encodedFrames.Count, runtimeBytes, demoBytes,
                (savings > 0 ? "Delta data is smaller than the optimised classic PNG atlas." : "The optimised classic PNG atlas is smaller; prefer classic export for this clip.") +
                (lossyTextures ? $" WebP quality {settings.WebpQuality}/100; visible error includes texture compression." : settings.DeltaTolerance == 0 ? " Lossless RGBA." : " Approximate export: compare playback with the original before use."),
                settings.DeltaTolerance, maximumError, (double)errorSum / ((long)width * height * encodedFrames.Count),
                100d * updatedPixels / ((long)width * height * encodedFrames.Count),
                lossyTextures ? "webp" : "png", lossyTextures ? settings.WebpQuality : null);
            // Normal exports contain playable assets only. Diagnostic bundles explicitly opt into the report/demo.
            if (runtimeDirectory != null)
                await File.WriteAllTextAsync(Path.Combine(staging, "delta.report.json"), JsonSerializer.Serialize(report, ProjectStore.JsonOptions), ct);
            await StagingFiles.DeleteDirectoryAsync(cache); await StagingFiles.DeleteDirectoryAsync(baseline);
            ct.ThrowIfCancellationRequested(); Directory.Move(staging, destination); return report;
        }
        finally { await StagingFiles.DeleteDirectoryAsync(staging); }
    }

    private static Candidate Evaluate(RgbaFrame frame, IReadOnlyList<RectI> regions, Dictionary<string, SpriteRecord> stored, CancellationToken ct)
    {
        var operations = new List<DeltaOperation>(); var payloads = new Dictionary<string, Payload>(); long bytes = 0;
        foreach (var rect in regions)
        {
            ct.ThrowIfCancellationRequested(); var image = AtlasExporter.Cut(frame, rect);
            if (image.Pixels.AsSpan().IndexOfAnyExcept((byte)0) < 0) { operations.Add(new("clear", rect.X, rect.Y, rect.Width, rect.Height)); continue; }
            string id = $"{rect.Width}x{rect.Height}-" + Convert.ToHexString(SHA256.HashData(image.Pixels));
            operations.Add(new("replace", rect.X, rect.Y, rect.Width, rect.Height, id));
            if (stored.ContainsKey(id) || payloads.ContainsKey(id)) continue;
            var png = Imaging.EncodePng(image); payloads.Add(id, new(id, image, png)); bytes += png.Length;
        }
        bytes += JsonSerializer.SerializeToUtf8Bytes(operations, CompactJson).Length;
        return new(operations, payloads, bytes);
    }
    private static Candidate Independent(RgbaFrame frame, Dictionary<string, SpriteRecord> stored, CancellationToken ct)
    {
        var best = Evaluate(frame, [new(0, 0, frame.Width, frame.Height)], stored, ct);
        // Checkpoints can reuse the same atlas blocks as delta frames; they need not store full images.
        foreach (int tile in new[] { 16, 32, 64, 128, 256 })
        {
            if ((long)((frame.Width + tile - 1) / tile) * ((frame.Height + tile - 1) / tile) > MaximumOperations) continue;
            var blocks = Blocks(null, frame.Width, frame.Height, tile, ct);
            var candidate = Evaluate(frame, blocks, stored, ct);
            if (candidate.Cost < best.Cost) best = candidate;
        }
        return best;
    }
    // Maximum display-channel error over both black and white, with alpha also bounded.
    // Affine alpha compositing means this bounds the error over any solid RGB background.
    private static int VisibleError(byte[] a, byte[] b, int i)
    {
        int alpha = a[i + 3], oldAlpha = b[i + 3], error = Math.Abs(alpha - oldAlpha) * 255;
        for (int c = 0; c < 3; c++)
        {
            error = Math.Max(error, Math.Abs(a[i + c] * alpha - b[i + c] * oldAlpha));
            error = Math.Max(error, Math.Abs((255 - a[i + c]) * alpha - (255 - b[i + c]) * oldAlpha));
        }
        return (error + 254) / 255;
    }
    private static (bool[] Mask, RectI? Bounds) Changes(RgbaFrame previous, RgbaFrame current, int tolerance, CancellationToken ct)
    {
        var mask = new bool[current.Width * current.Height]; int l = current.Width, t = current.Height, r = -1, b = -1;
        for (int y = 0; y < current.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < current.Width; x++)
            {
                int pixel = y * current.Width + x, i = pixel * 4;
                if (tolerance == 0 ? previous.Pixels.AsSpan(i, 4).SequenceEqual(current.Pixels.AsSpan(i, 4)) :
                    VisibleError(previous.Pixels, current.Pixels, i) <= tolerance &&
                    // Always clear disappearing pixels and write newly visible pixels: no alpha ghosts.
                    (previous.Pixels[i + 3] == 0) == (current.Pixels[i + 3] == 0)) continue;
                mask[pixel] = true; l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y);
            }
        }
        return (mask, r < 0 ? null : new(l, t, r - l + 1, b - t + 1));
    }
    private static List<RectI> Blocks(bool[]? changed, int width, int height, int tile, CancellationToken ct)
    {
        var result = new List<RectI>();
        for (int y = 0; y < height; y += tile)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x += tile)
            {
                var rect = new RectI(x, y, Math.Min(tile, width - x), Math.Min(tile, height - y));
                bool dirty = changed == null;
                for (int yy = y; yy < y + rect.Height && !dirty; yy++)
                    for (int xx = x; xx < x + rect.Width; xx++) if (changed![yy * width + xx]) { dirty = true; break; }
                if (dirty) result.Add(rect);
            }
        }
        return result;
    }
    private static RectI Tighten(RectI rect, bool[] mask, int width)
    {
        int l = rect.X + rect.Width, t = rect.Y + rect.Height, r = rect.X, b = rect.Y;
        for (int y = rect.Y; y < rect.Y + rect.Height; y++)
            for (int x = rect.X; x < rect.X + rect.Width; x++) if (mask[y * width + x])
            { l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x + 1); b = Math.Max(b, y + 1); }
        return new(l, t, r - l, b - t);
    }
    private sealed record Split(RectI First, RectI Second, long Saved);
    private static IEnumerable<List<RectI>> SparsePartitions(bool[] mask, int width, RectI bounds, CancellationToken ct)
    {
        var regions = new List<RectI> { bounds };
        var splits = new List<Split?> { BestSplit(bounds) };
        while (regions.Count < MaximumOperations)
        {
            ct.ThrowIfCancellationRequested(); int best = -1; long saved = 64;
            for (int i = 0; i < splits.Count; i++) if (splits[i] is { } s && s.Saved > saved) { best = i; saved = s.Saved; }
            if (best < 0) break;
            var split = splits[best]!;
            regions[best] = split.First; splits[best] = BestSplit(split.First);
            regions.Add(split.Second); splits.Add(BestSplit(split.Second));
            if (regions.Count is 16 or 32 or 64) yield return regions.ToList();
        }
        yield return regions;

        Split? BestSplit(RectI r)
        {
            Split? best = null;
            // Prefix/suffix bounding boxes find cuts that exclude the most unchanged interior.
            // Unlike a dirty tile grid, small distant contour changes need not force a large block.
            foreach (bool vertical in new[] { false, true })
            {
                int length = vertical ? r.Width : r.Height;
                var lines = new RectI?[length];
                for (int y = r.Y; y < r.Y + r.Height; y++)
                {
                    ct.ThrowIfCancellationRequested();
                    for (int x = r.X; x < r.X + r.Width; x++) if (mask[y * width + x])
                    {
                        int line = vertical ? x - r.X : y - r.Y;
                        lines[line] = Union(lines[line], new(x, y, 1, 1));
                    }
                }
                var suffix = new RectI?[length + 1];
                for (int j = length - 1; j >= 0; j--) suffix[j] = Union(lines[j], suffix[j + 1]);
                RectI? prefix = null;
                for (int j = 0; j < length - 1; j++)
                {
                    prefix = Union(prefix, lines[j]);
                    if (prefix is not { } a || suffix[j + 1] is not { } b) continue;
                    long gain = (long)r.Width * r.Height - (long)a.Width * a.Height - (long)b.Width * b.Height;
                    if (gain > (best?.Saved ?? 0)) best = new(a, b, gain);
                }
            }
            return best;
        }
        static RectI? Union(RectI? a, RectI? b)
        {
            if (a == null) return b; if (b == null) return a;
            var x = a.Value; var y = b.Value; int l = Math.Min(x.X, y.X), t = Math.Min(x.Y, y.Y);
            return new(l, t, Math.Max(x.X + x.Width, y.X + y.Width) - l, Math.Max(x.Y + x.Height, y.Y + y.Height) - t);
        }
    }
    private static List<RectI> TileRectangles(bool[] changed, int width, int height, int tile, CancellationToken ct)
    {
        var result = new List<RectI>(); var previous = new Dictionary<(int, int), int>();
        for (int y = 0; y < height; y += tile)
        {
            ct.ThrowIfCancellationRequested(); var active = new Dictionary<(int, int), int>(); int start = -1;
            for (int x = 0; x <= width; x += tile)
            {
                bool dirty = false;
                if (x < width)
                    for (int yy = y; yy < Math.Min(y + tile, height) && !dirty; yy++)
                        for (int xx = x; xx < Math.Min(x + tile, width); xx++) if (changed[yy * width + xx]) { dirty = true; break; }
                if (dirty && start < 0) start = x;
                if ((dirty || start < 0) && x + tile < width) continue;
                if (start < 0) continue;
                int right = dirty ? Math.Min(width, x + tile) : Math.Min(width, x); var key = (start, right);
                if (previous.TryGetValue(key, out int index)) { var r = result[index]; result[index] = r with { Height = r.Height + Math.Min(tile, height - y) }; active[key] = index; }
                else { active[key] = result.Count; result.Add(new(start, y, right - start, Math.Min(tile, height - y))); }
                start = -1;
            }
            previous = active;
        }
        return result;
    }
}

public sealed class DeltaAtlasPlayer : IAnimationSource
{
    private readonly string _directory;
    private readonly FrameCache _cache = new(256L * 1024 * 1024);
    private readonly object _gate = new();
    private byte[]? _canvas;
    private int _current = -1;
    public DeltaManifest Manifest { get; }
    public AnimationInfo Info { get; }
    public DeltaReport? Report { get; }
    public DeltaAtlasPlayer(string path)
    {
        _directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        try
        {
            Manifest = JsonSerializer.Deserialize<DeltaManifest>(File.ReadAllText(path), ProjectStore.JsonOptions) ?? throw new InvalidDataException("Empty delta manifest.");
            Validate();
            Info = new(Manifest.Width, Manifest.Height, Manifest.Fps, Manifest.Frames.Select(f => f.DurationMs).ToArray());
            string report = Path.Combine(_directory, "delta.report.json");
            if (File.Exists(report)) Report = JsonSerializer.Deserialize<DeltaReport>(File.ReadAllText(report), ProjectStore.JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NullReferenceException or ArgumentException or OverflowException) { throw new InvalidDataException("Invalid delta manifest.", ex); }
    }
    private string SafePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Delta paths must be relative.");
        var full = Path.GetFullPath(relative, _directory);
        if (!full.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Delta paths must stay inside the animation folder.");
        return full;
    }
    private void Validate()
    {
        var m = Manifest;
        if (m.Format != DeltaAtlasExporter.Format || m.Version != 1 || m.Width <= 0 || m.Height <= 0 || (long)m.Width * m.Height > 67108864 || !double.IsFinite(m.Fps) || m.Fps < 1 || m.Fps > 240 || m.Frames.Count == 0 || m.Keyframes.Count == 0 || m.Keyframes[0] != 0) throw new InvalidDataException("Unsupported delta manifest.");
        if (m.Encoding is { } encoding && (encoding.Tolerance is < 0 or > 32 || encoding.TextureFormat is not ("png" or "webp") ||
            (encoding.TextureFormat == "webp" ? encoding.Mode != "lossy" || encoding.TextureQuality is not (>= 1 and <= 100) :
                encoding.Mode != (encoding.Tolerance == 0 ? "lossless" : "bounded-error") || encoding.TextureQuality != null)))
            throw new InvalidDataException("Invalid delta encoding metadata.");
        foreach (var p in m.Pages) { _ = SafePath(p.Image); if (p.Width <= 0 || p.Height <= 0 || p.Width > 8192 || p.Height > 8192) throw new InvalidDataException("Invalid delta page dimensions."); }
        foreach (var p in m.Patches.Values)
        {
            if (p.Page < 0 || p.Page >= m.Pages.Count) throw new InvalidDataException("Invalid delta page reference.");
            FrameGeometry.ValidateCrop(p.Rect, m.Pages[p.Page].Width, m.Pages[p.Page].Height);
        }
        int lastKey = -DeltaAtlasExporter.KeyframeInterval;
        var actualKeys = new List<int>();
        for (int i = 0; i < m.Frames.Count; i++)
        {
            var f = m.Frames[i];
            if (!double.IsFinite(f.DurationMs) || Math.Abs(f.DurationMs - 1000 / m.Fps) > .000001 || f.Operations.Count > DeltaAtlasExporter.MaximumOperations || f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid delta frame timing, hash or operation count.");
            if (f.Keyframe) { actualKeys.Add(i); lastKey = i; }
            if (i - lastKey >= DeltaAtlasExporter.KeyframeInterval) throw new InvalidDataException("Missing delta checkpoint.");
            foreach (var op in f.Operations)
            {
                FrameGeometry.ValidateCrop(new(op.X, op.Y, op.Width, op.Height), m.Width, m.Height);
                if (op.Kind == "clear") { if (op.Patch != null) throw new InvalidDataException("Clear cannot reference a patch."); }
                else if (op.Kind != "replace" || op.Patch == null || !m.Patches.TryGetValue(op.Patch, out var patch) || patch.Rect.Width != op.Width || patch.Rect.Height != op.Height) throw new InvalidDataException("Invalid delta operation.");
            }
        }
        if (!actualKeys.SequenceEqual(m.Keyframes)) throw new InvalidDataException("Invalid delta checkpoint index.");
    }
    public RgbaFrame ReadFrame(int index, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested(); index = Math.Clamp(index, 0, Info.FrameCount - 1);
            try
            {
                if (_canvas == null || index < _current || index - _current >= DeltaAtlasExporter.KeyframeInterval)
                {
                    int key = Manifest.Keyframes.Last(k => k <= index); _canvas = new byte[checked(Info.Width * Info.Height * 4)]; _current = key - 1;
                }
                for (int i = _current + 1; i <= index; i++)
                {
                    ct.ThrowIfCancellationRequested(); var frame = Manifest.Frames[i];
                    if (frame.Keyframe) Array.Clear(_canvas);
                    foreach (var op in frame.Operations)
                    {
                        ct.ThrowIfCancellationRequested(); RgbaFrame? page = null; RectI rect = default;
                        if (op.Kind == "replace")
                        {
                            var patch = Manifest.Patches[op.Patch!]; var entry = Manifest.Pages[patch.Page]; rect = patch.Rect;
                            page = _cache.Get(entry.Image);
                            if (page == null)
                            {
                                page = Imaging.Load(SafePath(entry.Image));
                                if (page.Width != entry.Width || page.Height != entry.Height) throw new InvalidDataException("Delta page dimensions do not match its manifest.");
                                _cache.Put(entry.Image, page);
                            }
                        }
                        for (int y = 0; y < op.Height; y++)
                        {
                            int dst = ((op.Y + y) * Info.Width + op.X) * 4;
                            if (page == null) Array.Clear(_canvas, dst, op.Width * 4);
                            else Buffer.BlockCopy(page.Pixels, ((rect.Y + y) * page.Width + rect.X) * 4, _canvas, dst, op.Width * 4);
                        }
                    }
                    _current = i;
                }
                return new(Info.Width, Info.Height, (byte[])_canvas.Clone());
            }
            catch { _canvas = null; _current = -1; throw; }
        }
    }
}
