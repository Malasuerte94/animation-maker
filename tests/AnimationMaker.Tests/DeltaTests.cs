using AnimationMaker.Core;
using AnimationMaker.Gpu;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Xunit;

namespace AnimationMaker.Tests;

public sealed class DeltaTests
{
    private static string Folder() => Path.Combine(Path.GetTempPath(), "AnimationMaker-tests", "delta-" + Guid.NewGuid().ToString("N"));
    private static async IAsyncEnumerable<RgbaFrame> Sequence(IEnumerable<RgbaFrame> frames)
    { foreach (var frame in frames) { yield return frame; await Task.Yield(); } }
    private static RgbaFrame Image(int width, int height, int seed)
    {
        var b = new byte[width * height * 4]; new Random(seed).NextBytes(b);
        return new(width, height, b);
    }
    [Fact]
    public async Task ExactRgbaIncludingInvisibleRgbHoldsClearsAndRandomSeeks()
    {
        var frames = new List<RgbaFrame>(); var previous = new RgbaFrame(97, 65, new byte[97 * 65 * 4]);
        for (int n = 0; n < 72; n++)
        {
            var frame = previous with { Pixels = (byte[])previous.Pixels.Clone() };
            if (n == 1 || n == 39) frame = Image(97, 65, 42);
            if (n == 37 || n == 65) Array.Clear(frame.Pixels);
            if (n is > 2 and < 37 || n is > 39 and < 65)
            {
                int x = n % 80, y = n % 50;
                for (int yy = y; yy < y + 3; yy++) for (int xx = x; xx < x + 7; xx++)
                { int i = (yy * 97 + xx) * 4; frame.Pixels[i] = (byte)n; frame.Pixels[i + 3] = (byte)(n % 2 == 0 ? 1 : 128); }
            }
            frames.Add(frame); previous = frame;
        }
        string path = Folder();
        var report = await DeltaAtlasExporter.ExportAsync(Sequence(frames), path, new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 0 }, 29.97, expectedFrames: frames.Count);
        var player = new DeltaAtlasPlayer(Path.Combine(path, "delta.animation.json"));
        Assert.Equal(frames.Count, report.VerifiedFrames); Assert.True(report.SavingsPercent > 0);
        Assert.Contains(player.Manifest.Frames, f => !f.Keyframe && f.Operations.Count == 0);
        Assert.Contains(player.Manifest.Frames, f => f.Keyframe && f.Operations.Count == 0);
        foreach (int i in Enumerable.Range(0, frames.Count).Concat(new[] { 71, 2, 35, 0, 70, 39, 38, 37, 1 }))
            Assert.Equal(frames[i].Pixels, player.ReadFrame(i).Pixels);
        Assert.All(player.Manifest.Keyframes.Zip(player.Manifest.Keyframes.Skip(1)), pair => Assert.InRange(pair.Second - pair.First, 1, 30));
        Assert.False(Directory.Exists(Path.Combine(path, ".classic"))); Assert.False(Directory.Exists(Path.Combine(path, ".patches")));
        Assert.Equal(report.DeltaBytes, new FileInfo(Path.Combine(path, "delta.animation.json")).Length + Directory.GetFiles(path, "delta-*.png").Sum(f => new FileInfo(f).Length));
        var copy = player.ReadFrame(1); Array.Clear(copy.Pixels); Assert.Equal(frames[1].Pixels, player.ReadFrame(1).Pixels);
    }
    [Fact]
    public async Task DistantChangesAreSplitIntoSmallPatchesIncludingPartialEdgeTiles()
    {
        var first = Image(257, 193, 78); var second = first with { Pixels = (byte[])first.Pixels.Clone() };
        foreach (var r in new[] { new RectI(8, 8, 8, 8), new RectI(240, 176, 8, 8), new RectI(256, 192, 1, 1), new RectI(48, 40, 8, 8) })
            for (int y = r.Y; y < r.Y + r.Height; y++) for (int x = r.X; x < r.X + r.Width; x++)
            {
                int i = (y * first.Width + x) * 4;
                second.Pixels[i] = 220; second.Pixels[i + 1] = 30; second.Pixels[i + 2] = 90; second.Pixels[i + 3] = 2;
            }
        string path = Folder(); await DeltaAtlasExporter.ExportAsync(Sequence([first, second]), path, new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 0 }, 30);
        var player = new DeltaAtlasPlayer(Path.Combine(path, "delta.animation.json"));
        Assert.False(player.Manifest.Frames[1].Keyframe); Assert.True(player.Manifest.Frames[1].Operations.Count > 1);
        Assert.True(player.Manifest.Frames[1].Operations.Sum(o => o.Width * o.Height) < first.Width * first.Height / 10);
        Assert.Equal(second.Pixels, player.ReadFrame(1).Pixels);
    }
    [Fact]
    public async Task AllTransparentNeedsNoTexturesAndKeepsEveryFrame()
    {
        var frame = new RgbaFrame(13, 9, new byte[13 * 9 * 4]); string path = Folder();
        await DeltaAtlasExporter.ExportAsync(Sequence(Enumerable.Repeat(frame, 65)), path, new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 0 }, 24);
        var player = new DeltaAtlasPlayer(Path.Combine(path, "delta.animation.json"));
        Assert.Empty(player.Manifest.Pages); Assert.Empty(player.Manifest.Patches); Assert.Equal(65, player.Info.FrameCount);
        foreach (int i in new[] { 64, 0, 30, 29, 60 }) Assert.Equal(frame.Pixels, player.ReadFrame(i).Pixels);
    }
    [Fact]
    public async Task MultiplePagesAndFragmentedChangesReconstructExactly()
    {
        var frames = new[] { Image(1100, 1100, 42), Image(1100, 1100, 43), Image(1100, 1100, 44) };
        string path = Folder(); await DeltaAtlasExporter.ExportAsync(Sequence(frames), path, new() { ImageFormat = AtlasImageFormat.Png, MaxAtlasSize = 2048, DeltaTolerance = 0 }, 30);
        var player = new DeltaAtlasPlayer(Path.Combine(path, "delta.animation.json"));
        Assert.True(player.Manifest.Pages.Count >= 3);
        for (int i = 0; i < 3; i++) Assert.Equal(frames[i].Pixels, player.ReadFrame(i).Pixels);
        Assert.All(player.Manifest.Frames, f => Assert.InRange(f.Operations.Count, 0, 128));
    }
    [Fact]
    public async Task CancellationAndFrameCountErrorsRemoveStagingWithoutReplacingDestination()
    {
        string path = Folder(); using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.Completed == 2) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeltaAtlasExporter.ExportAsync(Sequence(Enumerable.Repeat(Image(32, 24, 42), 70)), path, new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 0 }, 30, progress, ct: cts.Token));
        Assert.False(Directory.Exists(path)); Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".staging-*"));
        await Assert.ThrowsAsync<InvalidDataException>(() => DeltaAtlasExporter.ExportAsync(Sequence([Image(32, 24, 42)]), path, new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 0 }, 30, expectedFrames: 2));
        Directory.CreateDirectory(path); await File.WriteAllTextAsync(Path.Combine(path, "keep.txt"), "keep");
        await Assert.ThrowsAsync<IOException>(() => DeltaAtlasExporter.ExportAsync(Sequence([Image(32, 24, 42)]), path, new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 0 }, 30));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(path, "keep.txt")));
    }
    [Fact]
    public async Task MalformedReferencesTimingCheckpointsAndPathsAreRejected()
    {
        string path = Folder(); await DeltaAtlasExporter.ExportAsync(Sequence([Image(16, 13, 42)]), path, new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 0 }, 30);
        string file = Path.Combine(path, "delta.animation.json"); var player = new DeltaAtlasPlayer(file);
        var m = player.Manifest with { Version = 1, Frames = player.Manifest.Frames.Select((f, i) => f with { Sha256 = Convert.ToHexString(SHA256.HashData(player.ReadFrame(i).Pixels)) }).ToList() };
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(m, ProjectStore.JsonOptions));
        Assert.Equal(player.ReadFrame(0).Pixels, new DeltaAtlasPlayer(file).ReadFrame(0).Pixels);
        var invalid = new[] {
            m with { Version = 3 }, m with { Keyframes = [] }, m with { Frames = [m.Frames[0] with { DurationMs = 0 }] },
            m with { Pages = [m.Pages[0] with { Image = "../escape.png" }] },
            m with { Frames = [m.Frames[0] with { Operations = [new("replace", 0, 0, 16, 13, "missing")] }] },
            m with { Frames = [m.Frames[0] with { Operations = [new("clear", 15, 0, 2, 13)] }] }
        };
        foreach (var bad in invalid)
        {
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(bad, ProjectStore.JsonOptions));
            Assert.Throws<InvalidDataException>(() => new DeltaAtlasPlayer(file));
        }
        await File.WriteAllTextAsync(file, "{}"); Assert.Throws<InvalidDataException>(() => new DeltaAtlasPlayer(file));
    }

    [Fact]
    public async Task CompactManifestRemovesHashesAndRepeatedMetadataAndStillReadsLegacyV1()
    {
        var first = Image(48, 32, 91); string path = Folder();
        await DeltaAtlasExporter.ExportAsync(Sequence(Enumerable.Repeat(first, 65)), path, new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 0 }, 29.97);
        string file = Path.Combine(path, "delta.animation.json"), compact = await File.ReadAllTextAsync(file);
        var player = new DeltaAtlasPlayer(file);
        Assert.Equal(2, player.Manifest.Version);
        Assert.DoesNotContain("sha256", compact); Assert.DoesNotContain("durationMs", compact); Assert.DoesNotContain("operations", compact);
        var legacy = player.Manifest with { Version = 1, Frames = player.Manifest.Frames.Select((f, i) => f with { Sha256 = Convert.ToHexString(SHA256.HashData(player.ReadFrame(i).Pixels)) }).ToList() };
        string original = JsonSerializer.Serialize(legacy, new JsonSerializerOptions(ProjectStore.JsonOptions) { WriteIndented = false });
        Assert.True(compact.Length < original.Length / 5);
        await File.WriteAllTextAsync(file, original);
        var oldPlayer = new DeltaAtlasPlayer(file);
        foreach (int i in new[] { 64, 0, 30, 29, 60 }) Assert.Equal(first.Pixels, oldPlayer.ReadFrame(i).Pixels);
    }

    [Fact]
    public async Task CompactManifestRejectsMalformedTuplesReferencesAndCheckpoints()
    {
        string path = Folder();
        await DeltaAtlasExporter.ExportAsync(Sequence([Image(16, 13, 42)]), path, new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 0 }, 30);
        string file = Path.Combine(path, "delta.animation.json"), valid = await File.ReadAllTextAsync(file);
        var mutations = new Action<JsonNode>[] {
            m => m["pages"]![0] = new JsonArray("delta-000.png", 10),
            m => m["patches"]![0] = new JsonArray(0, 0, 0, 16),
            m => m["frames"]![0] = new JsonArray(99999, 0, 0),
            m => m["frames"]![0] = new JsonArray(0, 0),
            m => m["frames"]![0] = new JsonArray(-2, 0, 0),
            m => m["frames"]![0] = new JsonArray(-1, 0, 0, 17, 13),
            m => m["frames"]![0] = new JsonArray(0, -1, 0),
            m => m["keyframes"] = new JsonArray(0, 0),
            m => m["keyframes"] = new JsonArray(1),
            m => m["fps"] = 0
        };
        foreach (var mutate in mutations)
        {
            var node = JsonNode.Parse(valid)!; mutate(node); await File.WriteAllTextAsync(file, node.ToJsonString());
            Assert.Throws<InvalidDataException>(() => new DeltaAtlasPlayer(file));
        }
    }

    [Fact]
    public async Task SparseTransparentFramesUseTightPatchesAndCompactPagesWithoutTrails()
    {
        var frames = Enumerable.Range(0, 35).Select(n => {
            var image = new RgbaFrame(320, 256, new byte[320 * 256 * 4]);
            if (n == 18) return image;
            foreach (var r in new[] { new RectI(12 + n * 2, 15, 13, 29), new RectI(270, 208 - n, 17, 11) })
                for (int y = r.Y; y < r.Y + r.Height; y++) for (int x = r.X; x < r.X + r.Width; x++)
                {
                    int i = (y * image.Width + x) * 4;
                    image.Pixels[i] = (byte)(n + x); image.Pixels[i + 1] = (byte)(y + n); image.Pixels[i + 3] = (byte)(1 + n * 7);
                }
            // Invisible RGB must survive trimming in exact mode too.
            image.Pixels[(100 * image.Width + 130) * 4] = 17;
            return image;
        }).ToArray();
        string path = Folder();
        var report = await DeltaAtlasExporter.ExportAsync(Sequence(frames), path, new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 0 }, 24);
        var player = new DeltaAtlasPlayer(Path.Combine(path, "delta.animation.json"));
        long payloadArea = player.Manifest.Patches.Values.Sum(p => (long)p.Rect.Width * p.Rect.Height);
        Assert.True(payloadArea < (long)320 * 256 * frames.Length / 8);
        long slots = player.Manifest.Patches.Values.Sum(p => (long)(p.Rect.Width + 2) * (p.Rect.Height + 2));
        Assert.True(report.AtlasPixels < slots * 1.7);
        foreach (int i in Enumerable.Range(0, frames.Length).Concat(new[] { 34, 0, 18, 30, 17 })) Assert.Equal(frames[i].Pixels, player.ReadFrame(i).Pixels);
    }
    private sealed class InlineProgress(Action<ExportProgress> action) : IProgress<ExportProgress>
    { public void Report(ExportProgress value) => action(value); }

    [Fact]
    public async Task BoundedErrorIgnoresNoiseWithoutAccumulationAndPreservesAlphaTransitions()
    {
        var frames = new List<RgbaFrame>();
        for (int n = 0; n < 67; n++)
        {
            var pixels = new byte[83 * 57 * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            { pixels[i] = (byte)(40 + n); pixels[i + 1] = 90; pixels[i + 2] = 120; pixels[i + 3] = 255; }
            // Even almost invisible new pixels must appear and disappearing pixels must clear.
            pixels[3] = (byte)(n % 2 == 0 ? 0 : 1);
            frames.Add(new(83, 57, pixels));
        }
        string path = Folder();
        var report = await DeltaAtlasExporter.ExportAsync(Sequence(frames), path, new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 8 }, 24);
        var player = new DeltaAtlasPlayer(Path.Combine(path, "delta.animation.json"));
        Assert.Equal(new("bounded-error", 8), player.Manifest.Encoding);
        Assert.InRange(report.MaximumVisibleError, 1, 8);
        Assert.InRange(report.MeanVisibleError, 0.01, 8);
        Assert.True(report.UpdatedPixelPercent < 40);
        foreach (int n in Enumerable.Range(0, frames.Count).Concat(new[] { 66, 1, 33, 0, 65 }))
        {
            var decoded = player.ReadFrame(n); Assert.Equal(frames[n].Pixels[3], decoded.Pixels[3]);
            for (int i = 4; i < decoded.Pixels.Length; i++) Assert.InRange(Math.Abs(decoded.Pixels[i] - frames[n].Pixels[i]), 0, 8);
        }
        Assert.Contains(player.Manifest.Frames, f => !f.Keyframe && f.Operations.Sum(o => o.Width * o.Height) < 10);
    }

    [Fact]
    public async Task TinyDistantMotionAndVideoNoiseProduceSparsePatchesInsteadOfFullCharacters()
    {
        var source = Image(277, 512, 72);
        for (int i = 3; i < source.Pixels.Length; i += 4) source.Pixels[i] = 255;
        var frames = Enumerable.Range(0, 10).Select(n =>
        {
            var pixels = (byte[])source.Pixels.Clone();
            for (int i = 0; i < pixels.Length; i += 4) pixels[i] = (byte)Math.Clamp(pixels[i] + n % 3 - 1, 0, 255);
            foreach (var r in new[] { new RectI(20, 10, 6, 6), new RectI(250, 490, 6, 6) })
                for (int y = r.Y; y < r.Y + r.Height; y++) for (int x = r.X; x < r.X + r.Width; x++)
                    pixels[(y * 277 + x) * 4] = (byte)(n * 23);
            return new RgbaFrame(277, 512, pixels);
        }).ToArray();
        string exact = Folder(), approximate = Folder();
        var original = await DeltaAtlasExporter.ExportAsync(Sequence(frames), exact, new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 0 }, 24);
        var report = await DeltaAtlasExporter.ExportAsync(Sequence(frames), approximate, new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 8 }, 24);
        var player = new DeltaAtlasPlayer(Path.Combine(approximate, "delta.animation.json"));
        Assert.True(report.DeltaBytes < original.DeltaBytes / 2);
        Assert.All(player.Manifest.Frames.Skip(1), frame => Assert.InRange(frame.Operations.Sum(o => o.Width * o.Height), 0, 1000));
        Assert.Contains(player.Manifest.Frames.Skip(1), frame => frame.Operations.Count > 1);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(33)]
    public async Task InvalidToleranceIsRejected(int tolerance)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => DeltaAtlasExporter.ExportAsync(Sequence([Image(16, 16, 42)]), Folder(), new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = tolerance }, 30));
    }

    [Fact]
    public async Task VideoExportKeysCropsAndScalesOnlyOnceForDeltaAndClassicComparison()
    {
        string root = Folder(); Directory.CreateDirectory(root); string clip = Path.Combine(root, "source.mp4"); var ffmpeg = new Ffmpeg();
        using (var generate = Ffmpeg.Start(ffmpeg.FfmpegPath, ["-v", "error", "-f", "lavfi", "-i", "color=green:s=96x64:r=12:d=1,drawbox=x=24:y=16:w=16:h=16:color=red:t=fill", "-c:v", "libx264", "-pix_fmt", "yuv420p", clip]))
        { var error = generate.StandardError.ReadToEndAsync(); await generate.WaitForExitAsync(); Assert.True(generate.ExitCode == 0, await error); }
        var info = await ffmpeg.ProbeAsync(clip);
        var project = new ProjectDocument { SourcePath = clip, TrimEnd = 1, Fps = 12, Crop = new(8, 8, 64, 40), Export = new() { ImageFormat = AtlasImageFormat.Png, MaxFrameHeight = 20, DeltaTolerance = 0 } };
        int calls = 0;
        var service = new ExportService(ffmpeg, () => new CountingProcessor(() => Interlocked.Increment(ref calls)));
        string output = Path.Combine(root, "output"); var report = await service.ExportDeltaAtlasAsync(project, info, output);
        Assert.Equal(12, calls); Assert.Equal(12, report.VerifiedFrames);
        var player = new DeltaAtlasPlayer(Path.Combine(output, "delta.animation.json")); Assert.Equal(32, player.Info.Width); Assert.Equal(20, player.Info.Height);
        using var gpu = new GpuFrameProcessor(); var source = await ffmpeg.ReadFrameAsync(clip, info, 0, 16384);
        var expected = gpu.Process(source, project.Keyer, project.Masks, 0, project.Crop.Value, project.OutputSize(info));
        Assert.Equal(expected.Pixels, player.ReadFrame(0).Pixels);
    }
    private sealed class CountingProcessor(Action count) : IFrameProcessor
    {
        private readonly GpuFrameProcessor _gpu = new();
        public RgbaFrame Process(RgbaFrame source, KeyerSettings keyer, IReadOnlyList<PolygonMask> masks, double time, RectI crop, SizeI output)
        { count(); return _gpu.Process(source, keyer, masks, time, crop, output); }
        public void Dispose() => _gpu.Dispose();
    }
}
