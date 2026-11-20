using AnimationMaker.Core;
using Xunit;

namespace AnimationMaker.Tests;

public sealed class PackedMp4Tests
{
    [Fact]
    public void PackPreservesRgbAndAlphaWithOddDimensions()
    {
        var frame = new RgbaFrame(511, 639, new byte[511 * 639 * 4]);
        byte[] color = [22, 33, 44, 0, 55, 66, 77, 128, 88, 99, 111, 255];
        int lastThreePixels = frame.Pixels.Length - color.Length;
        color.CopyTo(frame.Pixels, lastThreePixels);
        var size = PackedAlphaVideo.EncodedSize(new(511, 639));
        Assert.Equal(new SizeI(1024, 640), size);
        var rgb = Enumerable.Repeat((byte)255, size.Width * size.Height * 3).ToArray();
        PackedAlphaVideo.Pack(frame, rgb);
        int row = 638 * size.Width * 3;
        Assert.Equal(new byte[] { 22, 33, 44, 55, 66, 77, 88, 99, 111, 0, 0, 0 }, rgb[(row + 508 * 3)..(row + 512 * 3)]);
        Assert.Equal(new byte[] { 0, 0, 0, 128, 128, 128, 255, 255, 255, 0, 0, 0 }, rgb[(row + 1020 * 3)..(row + 1024 * 3)]);
        Assert.All(rgb[(639 * size.Width * 3)..], pixel => Assert.Equal(0, pixel));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RoundtripPreservesMotionPartialAlphaTrimAndOptionalAudio(bool sourceHasAudio, bool keepAudio)
    {
        using var directory = new TestDirectory();
        var ffmpeg = new Ffmpeg();
        string source = await GenerateSourceAsync(ffmpeg, directory.Path, sourceHasAudio);
        string output = Path.Combine(directory.Path, "motion.mp4");
        var project = new ProjectDocument
        {
            SourcePath = source, TrimStart = .1, TrimEnd = .4, Fps = 29.97,
            Crop = new(1, 1, 95, 63), Export = new() { IncludeAudio = keepAudio }
        };
        var info = await ffmpeg.ProbeAsync(source);
        Assert.Null(info.PackedAlphaSize);
        var service = new ExportService(ffmpeg, () => new MovingMatte(project.TrimStart, project.Fps));
        await service.ExportPackedMp4Async(project, info, output);
        string renamed = Path.Combine(directory.Path, "renamed.mp4");
        File.Move(output, renamed);
        var encoded = await ffmpeg.ProbeAsync(renamed);
        Assert.Equal("h264", encoded.CodecName);
        Assert.Equal(sourceHasAudio && keepAudio, encoded.HasAudio);
        Assert.Equal(new SizeI(95, 63), encoded.PackedAlphaSize);
        Assert.Equal((192, 64), (encoded.Width, encoded.Height));
        Assert.Equal(project.Fps, encoded.Fps, 3);

        int index = 0;
        await foreach (var packed in ffmpeg.DecodeAsync(renamed, encoded, 0, encoded.Duration, encoded.Fps, hardware: false))
        {
            var frame = PackedAlphaVideo.Unpack(packed, encoded.PackedAlphaSize!.Value);
            int start = MovingMatte.LeftEdge(index);
            Assert.InRange(AlphaAt(frame, start - 3), 0, 5);
            Assert.InRange(AlphaAt(frame, start + 6), 123, 133);
            Assert.InRange(AlphaAt(frame, start + 20), 250, 255);
            Assert.InRange(AlphaAt(frame, start + 32), 0, 5);
            int edge = Enumerable.Range(0, frame.Width).First(x => AlphaAt(frame, x) >= 64);
            Assert.Equal(start, edge);
            int offset = (32 * frame.Width + start + 6) * 4;
            Assert.InRange((int)frame.Pixels[offset], 205, 225);
            Assert.InRange((int)frame.Pixels[offset + 1], 46, 66);
            Assert.InRange((int)frame.Pixels[offset + 2], 14, 34);
            index++;
        }
        Assert.Equal(9, index);
    }

    [Fact]
    public async Task CancellingAfterFirstFramePreservesDestinationAndCleansStaging()
    {
        using var directory = new TestDirectory();
        var ffmpeg = new Ffmpeg();
        string source = await GenerateSourceAsync(ffmpeg, directory.Path, false);
        string output = Path.Combine(directory.Path, "existing.mp4");
        await File.WriteAllTextAsync(output, "previous export");
        var info = await ffmpeg.ProbeAsync(source);
        var project = new ProjectDocument { SourcePath = source, TrimEnd = .4 };
        using var cancellation = new CancellationTokenSource();
        var service = new ExportService(ffmpeg, () => new MovingMatte(0, project.Fps));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportPackedMp4Async(project, info, output,
            new ImmediateProgress(progress => { if (progress.Completed == 1) cancellation.Cancel(); }), cancellation.Token));
        Assert.Equal("previous export", await File.ReadAllTextAsync(output));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.staging-*"));
    }

    private static int AlphaAt(RgbaFrame frame, int x) => frame.Pixels[(32 * frame.Width + x) * 4 + 3];

    private static async Task<string> GenerateSourceAsync(Ffmpeg ffmpeg, string directory, bool includeAudio)
    {
        string path = Path.Combine(directory, "source.mp4");
        var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", "color=green:s=96x64:r=30:d=0.6" };
        if (includeAudio) args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:duration=0.6", "-c:a", "aac"]);
        args.AddRange(["-c:v", "libx264", "-pix_fmt", "yuv420p", path]);
        using var process = Ffmpeg.Start(ffmpeg.FfmpegPath, args);
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await errors);
        return path;
    }

    private sealed class MovingMatte(double start, double fps) : IFrameProcessor
    {
        public static int LeftEdge(int frame) => 8 + frame * 4;

        public RgbaFrame Process(RgbaFrame source, KeyerSettings keyer, IReadOnlyList<PolygonMask> masks, double time, RectI crop, SizeI output)
        {
            var pixels = new byte[output.Width * output.Height * 4];
            int left = LeftEdge((int)Math.Round((time - start) * fps));
            for (int y = 0; y < output.Height; y++)
                for (int x = left; x < left + 30; x++)
                {
                    int i = (y * output.Width + x) * 4;
                    pixels[i] = 215;
                    pixels[i + 1] = 56;
                    pixels[i + 2] = 24;
                    pixels[i + 3] = x < left + 15 ? (byte)128 : (byte)255;
                }
            return new(output.Width, output.Height, pixels);
        }
        public void Dispose() { }
    }

    private sealed class ImmediateProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("AnimationMaker-packed-mp4-");
        public string Path => _directory.FullName;
        public void Dispose() => _directory.Delete(recursive: true);
    }
}
