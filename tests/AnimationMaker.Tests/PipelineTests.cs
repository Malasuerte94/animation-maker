using System.Diagnostics;
using AnimationMaker.Core;
using AnimationMaker.Gpu;
using Xunit;

namespace AnimationMaker.Tests;

public sealed class PipelineTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/tests"));
    private static string Temp(string name) { Directory.CreateDirectory(Root); return Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")); }
    private static RgbaFrame Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var bytes = new byte[w * h * 4];
        for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = r; bytes[i + 1] = g; bytes[i + 2] = b; bytes[i + 3] = a; }
        return new(w, h, bytes);
    }
    private static async IAsyncEnumerable<RgbaFrame> Sequence(IEnumerable<RgbaFrame> frames)
    {
        foreach (var frame in frames) { yield return frame; await Task.Yield(); }
    }
    [Fact]
    public void HeightLimitPreservesCanvasAndNeverUpscales()
    {
        Assert.Equal(new SizeI(910, 512), FrameGeometry.FitHeight(new(1920, 1080), 512));
        Assert.Equal(new SizeI(100, 100), FrameGeometry.FitHeight(new(100, 100), 512));
        Assert.Equal(30, FrameGeometry.FrameCount(0, 1, 29.97));
        Assert.Throws<InvalidDataException>(() => FrameGeometry.ValidateCrop(new(0, 0, 200, 300), 100, 100));
    }
    [Fact]
    public void MaskKeyframesInterpolateAndRemoveWins()
    {
        var a = new List<PointD> { new(0, 0), new(.5, 0), new(.5, 1), new(0, 1) };
        var b = a.Select(p => new PointD(p.X + .5, p.Y)).ToList();
        var mask = new PolygonMask { Kind = MaskKind.Keep, Keyframes = [new(0, a), new(1, b)] };
        Assert.Equal(.25, mask.At(.5)[0].X);
        var pixels = Imaging.RasterizeMasks(8, 4, [mask, mask with { Kind = MaskKind.Remove }], .5);
        Assert.Equal(255, pixels[(1 * 8 + 3) * 4]); Assert.Equal(255, pixels[(1 * 8 + 3) * 4 + 1]);
    }
    [Fact]
    public void FrameCacheEvictsByBytesAndPromotesReads()
    {
        var cache = new FrameCache(32); var frame = Solid(2, 2, 1, 2, 3);
        cache.Put("a", frame); cache.Put("b", frame); Assert.NotNull(cache.Get("a")); cache.Put("c", frame);
        Assert.Null(cache.Get("b")); Assert.Equal(32, cache.UsedBytes);
    }
    [Fact]
    public void HistoryDoesNotExposeRedoAfterNewEdit()
    {
        var history = new EditHistory<KeyerSettings>(new()); history.Set(history.Current with { Despill = .3f }); history.Undo();
        Assert.True(history.CanRedo); history.Set(history.Current with { Despill = .7f }); Assert.False(history.CanRedo);
    }
    [Fact]
    public void GpuPreservesNeutralPartialAlphaAndProtectedGreen()
    {
        using var gpu = new GpuFrameProcessor();
        var input = Solid(64, 32, 128, 255, 128);
        var settings = new KeyerSettings { ClipBlack = 0, ClipWhite = 1 };
        var result = gpu.Process(input, settings, [], 0, new(0, 0, 64, 32), new(64, 32));
        Assert.InRange((int)result.Pixels[3], 126, 130);
        Assert.InRange((int)result.Pixels[0], 253, 255); Assert.Equal(result.Pixels[0], result.Pixels[1]);
        var green = Solid(64, 32, 0, 255, 0);
        var keep = new PolygonMask { Kind = MaskKind.Keep, Keyframes = [new(0, [new(0, 0), new(1, 0), new(1, 1), new(0, 1)])] };
        result = gpu.Process(green, settings, [keep], 0, new(0, 0, 64, 32), new(64, 32));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, result.Pixels[..4]);
        result = gpu.Process(green, settings, [], 0, new(0, 0, 64, 32), new(64, 32));
        Assert.All(result.Pixels, value => Assert.Equal(0, value));
    }
    [Theory]
    [InlineData(0f)]
    [InlineData(.5f)]
    [InlineData(1.5f)]
    public void GpuAlphaAwareResamplingDoesNotCreateGreenOrBlackFringes(float feather)
    {
        using var gpu = new GpuFrameProcessor();
        var frame = Solid(64, 64, 0, 255, 0);
        for (int y = 16; y < 48; y++) for (int x = 16; x < 48; x++)
        { int i = (y * 64 + x) * 4; frame.Pixels[i] = 255; frame.Pixels[i + 1] = 255; frame.Pixels[i + 2] = 255; }
        var result = gpu.Process(frame, new() { ClipBlack = 0, ClipWhite = 1, Feather = feather }, [], 0, new(0, 0, 64, 64), new(23, 23));
        for (int i = 0; i < result.Pixels.Length; i += 4)
            if (result.Pixels[i + 3] > 2) { Assert.InRange((int)result.Pixels[i], 253, 255); Assert.Equal(result.Pixels[i], result.Pixels[i + 1]); }
    }
    [Fact]
    public async Task PackedAndGridAtlasesReconstructEveryFrameExactly()
    {
        var frames = new List<RgbaFrame> { Solid(64, 48, 0, 0, 0, 0) };
        for (int n = 0; n < 6; n++)
        {
            var frame = Solid(64, 48, 0, 0, 0, 0);
            for (int y = 8; y < 24; y++) for (int x = 8 + n; x < 24 + n; x++) { int i = (y * 64 + x) * 4; frame.Pixels[i] = 200; frame.Pixels[i + 3] = (byte)(100 + y); }
            frames.Add(frame);
        }
        frames.Add(frames[2]);
        foreach (var layout in new[] { AtlasLayout.Packed, AtlasLayout.Grid })
        {
            string path = Temp(layout.ToString());
            await AtlasExporter.ExportAsync(Sequence(frames), path, new() { Layout = layout, ImageFormat = AtlasImageFormat.Png }, 29.97, expectedFrames: frames.Count);
            var player = new AtlasPlayer(Path.Combine(path, "animation.json"));
            Assert.Equal(frames.Count, player.Animation.Frames.Count);
            for (int i = 0; i < frames.Count; i++) Assert.Equal(frames[i].Pixels, player.ReadFrame(i).Pixels);
        }
    }
    [Fact]
    public async Task MultiPageAtlasPreservesTimingAndOffsets()
    {
        var frames = new[] { Solid(1100, 1100, 255, 0, 0), Solid(1100, 1100, 0, 0, 255) };
        string path = Temp("multipage");
        await AtlasExporter.ExportAsync(Sequence(frames), path, new() { MaxAtlasSize = 2048, ImageFormat = AtlasImageFormat.Png }, 24, expectedFrames: 2);
        Assert.Equal(2, Directory.GetFiles(path, "atlas-*.png").Length);
        var player = new AtlasPlayer(Path.Combine(path, "animation.json")); Assert.Equal(1000d / 24, player.Animation.Frames[1].DurationMs);
        Assert.Equal(frames[1].Pixels, player.ReadFrame(1).Pixels);
    }
    [Fact]
    public async Task CancellationRemovesStagedAtlas()
    {
        string path = Temp("cancel"); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtlasExporter.ExportAsync(Sequence([Solid(4, 4, 1, 2, 3)]), path, new(), 30, ct: cts.Token));
        Assert.False(Directory.Exists(path)); Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".staging-*"));
    }
    [Fact]
    public async Task WebmRoundtripIncludesTransparentPartialAndOpaquePixels()
    {
        var ffmpeg = new Ffmpeg();
        string folder = Temp("webm"); Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "source.mp4");
        using (var generator = Ffmpeg.Start(ffmpeg.FfmpegPath, ["-v", "error", "-f", "lavfi", "-i", "color=green:s=96x64:r=12:d=1", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source]))
        {
            var err = generator.StandardError.ReadToEndAsync(); await generator.WaitForExitAsync(); Assert.True(generator.ExitCode == 0, await err);
        }
        var info = await ffmpeg.ProbeAsync(source);
        var project = new ProjectDocument { SourcePath = source, TrimEnd = 1, Fps = 12, Keyer = new() { ClipBlack = 0, ClipWhite = 1 }, Export = new() { MaxFrameHeight = 64, VideoQuality = VideoQuality.Lossless, IncludeAudio = true } };
        var service = new ExportService(ffmpeg, () => new SyntheticAlphaProcessor());
        string output = Path.Combine(folder, "alpha.webm"); await service.ExportWebmAsync(project, info, output);
        var encoded = await ffmpeg.ProbeAsync(output); Assert.True(encoded.HasAlpha); Assert.True(encoded.HasAudio);
        int count = 0;
        await foreach (var frame in ffmpeg.DecodeAsync(output, encoded, 0, 1, 12, hardware: false))
        {
            Assert.Equal(0, frame.Pixels[3]); Assert.InRange((int)frame.Pixels[(frame.Width / 2) * 4 + 3], 126, 130); Assert.Equal(255, frame.Pixels[(frame.Width - 1) * 4 + 3]); count++;
        }
        Assert.Equal(12, count);
    }
    private sealed class SyntheticAlphaProcessor : IFrameProcessor
    {
        public RgbaFrame Process(RgbaFrame source, KeyerSettings keyer, IReadOnlyList<PolygonMask> masks, double time, RectI crop, SizeI output)
        {
            var result = Solid(output.Width, output.Height, 255, 255, 255);
            for (int y = 0; y < result.Height; y++) for (int x = 0; x < result.Width; x++) result.Pixels[(y * result.Width + x) * 4 + 3] = x < result.Width / 3 ? (byte)0 : x < result.Width * 2 / 3 ? (byte)128 : (byte)255;
            return result;
        }
        public void Dispose() { }
    }
}
