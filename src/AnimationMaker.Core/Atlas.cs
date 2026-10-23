using System.Security.Cryptography;
using System.Text.Json;

namespace AnimationMaker.Core;

public sealed record AtlasFrameData(RectI Frame, bool Rotated, bool Trimmed, RectI SpriteSourceSize, SizeI SourceSize);
public sealed record AnimationFrame(string Name, string Atlas, double DurationMs);
public sealed record AnimationManifest(int Version, double Fps, int Width, int Height, List<AnimationFrame> Frames);
internal sealed record SpriteRecord(string Name, string File, RectI Bounds, SizeI SourceSize, string Hash);
internal sealed record PlacedSprite(SpriteRecord Sprite, RectI Slot);
internal sealed class AtlasPage
{
    private readonly List<RectI> _free;
    public AtlasPage(int limit) : this(limit, limit) { }
    public AtlasPage(int width, int height) => _free = [new(0, 0, width, height)];
    public List<PlacedSprite> Sprites { get; } = [];
    public int Width { get; private set; }
    public int Height { get; private set; }
    public void PlaceFixed(SpriteRecord sprite, RectI slot)
    {
        Sprites.Add(new(sprite, slot)); Width = Math.Max(Width, slot.X + slot.Width); Height = Math.Max(Height, slot.Y + slot.Height);
    }
    public bool TryPlace(SpriteRecord sprite, int width, int height)
    {
        RectI? candidate = null; int bestShort = int.MaxValue, bestLong = int.MaxValue;
        foreach (var free in _free)
        {
            if (width > free.Width || height > free.Height) continue;
            int small = Math.Min(free.Width - width, free.Height - height), large = Math.Max(free.Width - width, free.Height - height);
            if (small < bestShort || (small == bestShort && large < bestLong)) { bestShort = small; bestLong = large; candidate = new(free.X, free.Y, width, height); }
        }
        if (candidate is not { } used) return false;
        for (int i = _free.Count - 1; i >= 0; i--)
        {
            var free = _free[i];
            if (used.X >= free.X + free.Width || used.X + used.Width <= free.X || used.Y >= free.Y + free.Height || used.Y + used.Height <= free.Y) continue;
            _free.RemoveAt(i);
            if (used.X > free.X) _free.Add(new(free.X, free.Y, used.X - free.X, free.Height));
            if (used.X + used.Width < free.X + free.Width) _free.Add(new(used.X + used.Width, free.Y, free.X + free.Width - used.X - used.Width, free.Height));
            if (used.Y > free.Y) _free.Add(new(free.X, free.Y, free.Width, used.Y - free.Y));
            if (used.Y + used.Height < free.Y + free.Height) _free.Add(new(free.X, used.Y + used.Height, free.Width, free.Y + free.Height - used.Y - used.Height));
        }
        for (int i = _free.Count - 1; i >= 0; i--)
            for (int j = 0; j < _free.Count; j++)
            {
                if (i == j) continue;
                var a = _free[i]; var b = _free[j];
                if (a.X >= b.X && a.Y >= b.Y && a.X + a.Width <= b.X + b.Width && a.Y + a.Height <= b.Y + b.Height) { _free.RemoveAt(i); break; }
            }
        Sprites.Add(new(sprite, used)); Width = Math.Max(Width, used.X + used.Width); Height = Math.Max(Height, used.Y + used.Height);
        return true;
    }
}

public static class AtlasExporter
{
    public static RectI FindBounds(RgbaFrame frame)
    {
        int minX = frame.Width, minY = frame.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
                if (frame.Pixels[(y * frame.Width + x) * 4 + 3] != 0) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        return maxX < 0 ? new(0, 0, 1, 1) : new(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
    public static RgbaFrame Cut(RgbaFrame source, RectI bounds)
    {
        var pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
        for (int y = 0; y < bounds.Height; y++) Buffer.BlockCopy(source.Pixels, ((bounds.Y + y) * source.Width + bounds.X) * 4, pixels, y * bounds.Width * 4, bounds.Width * 4);
        return new(bounds.Width, bounds.Height, pixels);
    }
    public static async Task ExportAsync(IAsyncEnumerable<RgbaFrame> frames, string destination, ExportSettings settings, double fps, IProgress<ExportProgress>? progress = null, int expectedFrames = 0, CancellationToken ct = default, Action<long>? stagedBytesChanged = null, ExportProfiler? profiler = null)
    {
        settings.ValidateImageEncoding();
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Choose a new output folder; an atlas already exists at this path.");
        string staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        string cache = Path.Combine(staging, ".frames"); Directory.CreateDirectory(cache);
        try
        {
            var sprites = new List<SpriteRecord>();
            long spoolBytes = 0; int frameIndex = 0;
            await foreach (var frame in frames.WithCancellation(ct))
            {
                frame.Validate();
                var bounds = settings.Layout == AtlasLayout.Grid ? new RectI(0, 0, frame.Width, frame.Height) : FindBounds(frame);
                var cut = Cut(frame, bounds);
                string hash = $"{cut.Width}x{cut.Height}-" + Convert.ToHexString(SHA256.HashData(cut.Pixels));
                bool reuse = settings.Deduplicate && settings.Layout == AtlasLayout.Packed;
                var file = Path.Combine(cache, reuse ? hash + ".png" : frameIndex + ".png");
                if (!File.Exists(file))
                {
                    using (profiler?.Measure("baseline_png_encode_and_write")) Imaging.SavePng(file, cut);
                    profiler?.Count("baseline_png_encodes");
                    spoolBytes += new FileInfo(file).Length; stagedBytesChanged?.Invoke(spoolBytes);
                }
                if (spoolBytes > 8L * 1024 * 1024 * 1024) throw new IOException("The frame cache exceeds 8 GB. Export a shorter interval or lower the frame height.");
                sprites.Add(new($"frame_{frameIndex:D6}", file, bounds, new(frame.Width, frame.Height), reuse ? hash : frameIndex.ToString()));
                frameIndex++; progress?.Report(new("Processing frames", frameIndex, expectedFrames));
            }
            ct.ThrowIfCancellationRequested();
            if (sprites.Count == 0) throw new InvalidDataException("There are no frames in the selected interval.");
            if (expectedFrames > 0 && sprites.Count != expectedFrames) throw new InvalidDataException($"Decoded {sprites.Count} of {expectedFrames} expected frames. Check the selected interval or source file.");
            int margin = settings.Padding + settings.Extrusion;
            var unique = sprites.DistinctBy(s => s.Hash).ToList();
            List<AtlasPage> pages;
            using (profiler?.Measure("baseline_packing")) pages = settings.Layout == AtlasLayout.Packed
                ? AtlasPacker.Pack(unique, settings.MaxAtlasSize, margin, ct, profiler: profiler) : new List<AtlasPage>();
            var locations = new Dictionary<string, (int Page, RectI Rect)>();
            if (settings.Layout == AtlasLayout.Packed)
                for (int p = 0; p < pages.Count; p++)
                    foreach (var placed in pages[p].Sprites)
                        locations[placed.Sprite.Hash] = (p, new(placed.Slot.X + margin, placed.Slot.Y + margin, placed.Sprite.Bounds.Width, placed.Sprite.Bounds.Height));
            foreach (var sprite in settings.Layout == AtlasLayout.Grid ? unique : [])
            {
                ct.ThrowIfCancellationRequested();
                int w = sprite.Bounds.Width + margin * 2, h = sprite.Bounds.Height + margin * 2;
                if (w > settings.MaxAtlasSize || h > settings.MaxAtlasSize) throw new InvalidDataException("A frame exceeds the atlas size. Lower frame height or increase atlas size.");
                int cols = settings.MaxAtlasSize / w, rows = settings.MaxAtlasSize / h;
                int item = locations.Count, capacity = cols * rows, index = item / capacity;
                if (index == pages.Count) pages.Add(new(settings.MaxAtlasSize));
                int slotIndex = item % capacity; pages[index].PlaceFixed(sprite, new(slotIndex % cols * w, slotIndex / cols * h, w, h));
                var slot = pages[index].Sprites[^1].Slot;
                locations[sprite.Hash] = (index, new(slot.X + margin, slot.Y + margin, sprite.Bounds.Width, sprite.Bounds.Height));
            }
            var phaserPages = new List<object>();
            for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var page = pages[pageIndex];
                var image = new RgbaFrame(page.Width, page.Height, new byte[checked(page.Width * page.Height * 4)]);
                foreach (var placement in page.Sprites)
                {
                    ct.ThrowIfCancellationRequested();
                    var sprite = Imaging.Load(placement.Sprite.File); int ox = placement.Slot.X + margin, oy = placement.Slot.Y + margin;
                    for (int y = -settings.Extrusion; y < sprite.Height + settings.Extrusion; y++)
                        for (int x = -settings.Extrusion; x < sprite.Width + settings.Extrusion; x++)
                            Buffer.BlockCopy(sprite.Pixels, (Math.Clamp(y, 0, sprite.Height - 1) * sprite.Width + Math.Clamp(x, 0, sprite.Width - 1)) * 4, image.Pixels, ((oy + y) * image.Width + ox + x) * 4, 4);
                }
                string pageName = $"atlas-{pageIndex:D3}";
                string imageName = pageName + settings.ImageExtension;
                using (profiler?.Measure("baseline_atlas_encode_and_write")) Imaging.SaveAtlasImage(Path.Combine(staging, imageName), image, settings);
                spoolBytes += new FileInfo(Path.Combine(staging, imageName)).Length;
                stagedBytesChanged?.Invoke(spoolBytes);
                if (spoolBytes > 8L * 1024 * 1024 * 1024) throw new IOException("The atlas staging exceeds 8 GiB. Export a shorter interval or lower frame height.");
                var data = new Dictionary<string, AtlasFrameData>();
                foreach (var sprite in sprites.Where(s => locations[s.Hash].Page == pageIndex))
                    data[sprite.Name] = new(locations[sprite.Hash].Rect, false, sprite.Bounds.X != 0 || sprite.Bounds.Y != 0 || sprite.Bounds.Width != sprite.SourceSize.Width || sprite.Bounds.Height != sprite.SourceSize.Height, sprite.Bounds, sprite.SourceSize);
                // TexturePacker uses w/h fields rather than the application's Width/Height names.
                object wire(AtlasFrameData f) => new { frame = RectWire(f.Frame), rotated = f.Rotated, trimmed = f.Trimmed, spriteSourceSize = RectWire(f.SpriteSourceSize), sourceSize = new { w = f.SourceSize.Width, h = f.SourceSize.Height } };
                var root = new { frames = data.ToDictionary(p => p.Key, p => wire(p.Value)), animations = new Dictionary<string, string[]> { ["animation"] = data.Keys.ToArray() }, meta = new { app = "Animation Maker", version = "1", image = imageName, format = "RGBA8888", size = new { w = page.Width, h = page.Height }, scale = "1", related_multi_packs = Enumerable.Range(0, pages.Count).Where(i => i != pageIndex).Select(i => $"atlas-{i:D3}.json").ToArray() } };
                await WriteJsonAsync(Path.Combine(staging, pageName + ".json"), root, ct);
                phaserPages.Add(new { image = imageName, format = "RGBA8888", size = new { w = page.Width, h = page.Height }, scale = 1, frames = data.Select(p => new { filename = p.Key, frame = RectWire(p.Value.Frame), rotated = false, trimmed = p.Value.Trimmed, spriteSourceSize = RectWire(p.Value.SpriteSourceSize), sourceSize = new { w = p.Value.SourceSize.Width, h = p.Value.SourceSize.Height } }).ToArray() });
                progress?.Report(new("Packing atlas pages", pageIndex + 1, pages.Count));
            }
            await WriteJsonAsync(Path.Combine(staging, "phaser.json"), new { textures = phaserPages }, ct);
            var manifest = new AnimationManifest(1, fps, sprites[0].SourceSize.Width, sprites[0].SourceSize.Height, sprites.Select(s => new AnimationFrame(s.Name, $"atlas-{locations[s.Hash].Page:D3}.json", 1000 / fps)).ToList());
            await WriteJsonAsync(Path.Combine(staging, "animation.json"), manifest, ct);
            spoolBytes += Directory.EnumerateFiles(staging, "*.json").Sum(f => new FileInfo(f).Length);
            stagedBytesChanged?.Invoke(spoolBytes);
            if (spoolBytes > 8L * 1024 * 1024 * 1024) throw new IOException("The atlas staging exceeds 8 GiB.");
            await StagingFiles.DeleteDirectoryAsync(cache);
            ct.ThrowIfCancellationRequested(); Directory.Move(staging, destination);
        }
        finally { await StagingFiles.DeleteDirectoryAsync(staging); }
    }
    private static object RectWire(RectI r) => new { x = r.X, y = r.Y, w = r.Width, h = r.Height };
    private static readonly JsonSerializerOptions CompactJson = new(ProjectStore.JsonOptions) { WriteIndented = false };
    private static Task WriteJsonAsync(string path, object value, CancellationToken ct) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, CompactJson), ct);
}

internal static class StagingFiles
{
    public static async Task DeleteDirectoryAsync(string ownedStagingPath)
    {
        // Windows indexers can briefly hold freshly written PNGs. Only caller-created staging paths reach this helper.
        for (int attempt = 0; Directory.Exists(ownedStagingPath); attempt++)
        {
            try { Directory.Delete(ownedStagingPath, true); return; }
            catch (IOException) when (attempt < 5) { await Task.Delay(50 << attempt); }
            catch (UnauthorizedAccessException) when (attempt < 5) { await Task.Delay(50 << attempt); }
        }
    }
}

public sealed class AtlasPlayer : IAnimationSource
{
    private readonly string _directory;
    private readonly Dictionary<string, (string Image, AtlasFrameData Data)> _frames = new();
    private readonly FrameCache _cache = new(256L * 1024 * 1024);
    public AnimationManifest Animation { get; }
    public AnimationInfo Info { get; }
    public AtlasPlayer(string path)
    {
        _directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var manifestPath = Path.Combine(_directory, "animation.json");
        Animation = JsonSerializer.Deserialize<AnimationManifest>(File.ReadAllText(manifestPath), ProjectStore.JsonOptions) ?? throw new InvalidDataException("Invalid animation manifest.");
        if (Animation.Version != 1 || Animation.Frames.Count == 0 || !double.IsFinite(Animation.Fps) || Animation.Fps <= 0) throw new InvalidDataException("Unsupported animation manifest.");
        Info = new(Animation.Width, Animation.Height, Animation.Fps, Animation.Frames.Select(f => f.DurationMs).ToArray());
        foreach (var atlas in Animation.Frames.Select(f => f.Atlas).Distinct())
        {
            using var json = JsonDocument.Parse(File.ReadAllText(SafePath(atlas)));
            string image = json.RootElement.GetProperty("meta").GetProperty("image").GetString()!;
            _ = SafePath(image);
            foreach (var item in json.RootElement.GetProperty("frames").EnumerateObject())
            {
                static RectI rect(JsonElement r) => new(r.TryGetProperty("x", out var x) ? x.GetInt32() : 0, r.TryGetProperty("y", out var y) ? y.GetInt32() : 0, r.GetProperty("w").GetInt32(), r.GetProperty("h").GetInt32());
                var data = item.Value; var size = data.GetProperty("sourceSize");
                _frames[item.Name] = (image, new(rect(data.GetProperty("frame")), false, data.GetProperty("trimmed").GetBoolean(), rect(data.GetProperty("spriteSourceSize")), new(size.GetProperty("w").GetInt32(), size.GetProperty("h").GetInt32())));
            }
        }
    }
    private string SafePath(string relative)
    {
        var path = Path.GetFullPath(relative, _directory);
        if (!path.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Atlas paths must stay inside the animation folder.");
        return path;
    }
    public RgbaFrame ReadFrame(int index, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        index = Math.Clamp(index, 0, Animation.Frames.Count - 1);
        var entry = _frames[Animation.Frames[index].Name];
        var page = _cache.Get(entry.Image);
        if (page == null) { page = Imaging.Load(SafePath(entry.Image)); _cache.Put(entry.Image, page); }
        var d = entry.Data;
        FrameGeometry.ValidateCrop(d.Frame, page.Width, page.Height);
        FrameGeometry.ValidateCrop(d.SpriteSourceSize, d.SourceSize.Width, d.SourceSize.Height);
        if (d.Frame.Width != d.SpriteSourceSize.Width || d.Frame.Height != d.SpriteSourceSize.Height) throw new InvalidDataException("Invalid trimmed frame dimensions.");
        var pixels = new byte[checked(d.SourceSize.Width * d.SourceSize.Height * 4)];
        for (int y = 0; y < d.Frame.Height; y++) Buffer.BlockCopy(page.Pixels, ((d.Frame.Y + y) * page.Width + d.Frame.X) * 4, pixels, ((d.SpriteSourceSize.Y + y) * d.SourceSize.Width + d.SpriteSourceSize.X) * 4, d.Frame.Width * 4);
        return new(d.SourceSize.Width, d.SourceSize.Height, pixels);
    }
}
