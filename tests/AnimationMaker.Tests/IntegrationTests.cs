using System.Text.Json;
using AnimationMaker.Core;
using AnimationMaker.Gpu;
using Xunit;

namespace AnimationMaker.Tests;

public sealed class IntegrationTests
{
    private static string Folder()
    {
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/tests/integration-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(directory); return directory;
    }
    private static async Task<string> GenerateAsync(Ffmpeg ffmpeg, string folder, string codec, string size, string rate = "30")
    {
        string output = Path.Combine(folder, "source.mp4");
        using var process = Ffmpeg.Start(ffmpeg.FfmpegPath, ["-v", "error", "-f", "lavfi", "-i", $"color=c=0x00ff00:s={size}:r={rate}:d=0.4", "-c:v", codec, "-preset", "ultrafast", "-pix_fmt", "yuv420p", output]);
        var errors = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await errors); return output;
    }
    [Theory]
    [InlineData("libx264", "320x180")]
    [InlineData("libx265", "3840x2160")]
    public async Task H264And4kHevcDecodeAndExportFractionalRange(string codec, string dimensions)
    {
        string folder = Folder(); var ffmpeg = new Ffmpeg(); string source = await GenerateAsync(ffmpeg, folder, codec, dimensions);
        var info = await ffmpeg.ProbeAsync(source); Assert.False(info.HasAlpha);
        var project = new ProjectDocument { SourcePath = source, TrimStart = .033, TrimEnd = .27, Fps = 29.97, Export = new() { MaxFrameHeight = 96 } };
        var service = new ExportService(ffmpeg, () => new GpuFrameProcessor()); int count = 0;
        await foreach (var frame in service.ProcessFramesAsync(project, info))
        {
            Assert.Equal(96, frame.Height); Assert.True(frame.Width > 96);
            Assert.All(frame.Pixels.Where((_, i) => i % 4 == 3), alpha => Assert.InRange((int)alpha, 0, 2)); count++;
        }
        Assert.Equal(FrameGeometry.FrameCount(.033, .27, 29.97), count);
    }
    [Fact]
    public async Task RotationMetadataIsAppliedBeforeSizingAndCrop()
    {
        string folder = Folder(); var ffmpeg = new Ffmpeg(); string source = await GenerateAsync(ffmpeg, folder, "libx264", "320x180");
        string rotated = Path.Combine(folder, "rotated.mp4");
        using (var process = Ffmpeg.Start(ffmpeg.FfmpegPath, ["-v", "error", "-display_rotation:v:0", "90", "-i", source, "-c", "copy", rotated])) { var errors = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await errors); }
        var info = await ffmpeg.ProbeAsync(rotated); Assert.Equal(180, info.Width); Assert.Equal(320, info.Height);
        var frame = await ffmpeg.ReadFrameAsync(rotated, info, 0, 160); Assert.Equal(90, frame.Width); Assert.Equal(160, frame.Height);
    }
    [Fact]
    public async Task AlphaMovUsesItsNativeDecoderRatherThanVp9()
    {
        string folder = Folder(); var ffmpeg = new Ffmpeg(); string path = Path.Combine(folder, "alpha.mov");
        using (var process = Ffmpeg.Start(ffmpeg.FfmpegPath, ["-v", "error", "-f", "lavfi", "-i", "color=c=red@0.5:s=96x64:r=12:d=0.4,format=rgba", "-c:v", "qtrle", path])) { var errors = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await errors); }
        var info = await ffmpeg.ProbeAsync(path); Assert.True(info.HasAlpha); Assert.Equal("qtrle", info.CodecName);
        var frame = await ffmpeg.ReadFrameAsync(path, info, 0); Assert.InRange((int)frame.Pixels[3], 126, 130); Assert.Equal(255, frame.Pixels[0]);
    }
    [Fact]
    public async Task VariableFrameRateNormalizesWithoutDroppingLastFrame()
    {
        string folder = Folder(); var ffmpeg = new Ffmpeg(); string path = Path.Combine(folder, "vfr.mp4");
        using (var process = Ffmpeg.Start(ffmpeg.FfmpegPath, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=s=160x90:r=30:d=1", "-vf", "select='if(lt(t,0.5),not(mod(n,2)),1)'", "-fps_mode", "vfr", "-c:v", "libx264", path])) { var errors = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await errors); }
        var info = await ffmpeg.ProbeAsync(path); int count = 0;
        await foreach (var frame in ffmpeg.DecodeAsync(path, info, 0, info.Duration, 24)) { frame.Validate(); count++; }
        Assert.Equal(FrameGeometry.FrameCount(0, info.Duration, 24), count);
    }
    [Fact]
    public async Task ProjectRoundtripRetainsRelativeMediaAndMaskKeyframes()
    {
        string folder = Folder(); string source = Path.Combine(folder, "source.mp4"); await File.WriteAllTextAsync(source, "fixture");
        var project = new ProjectDocument { SourcePath = source, TrimEnd = 2, Masks = [new() { Keyframes = [new(0, [new(0, 0), new(1, 0), new(0, 1)]), new(1, [new(.1, 0), new(1, 0), new(.1, 1)])] }] };
        string path = Path.Combine(folder, "project.animproj"); await ProjectStore.SaveAsync(path, project);
        Assert.Contains("\"sourcePath\": \"source.mp4\"", await File.ReadAllTextAsync(path));
        var loaded = await ProjectStore.LoadAsync(path); Assert.Equal(source, loaded.SourcePath); Assert.Equal(.05, loaded.Masks[0].At(.5)[0].X);
    }
    [Fact]
    public async Task ClassicGridIsRowMajorAndKeepsDuplicateFrames()
    {
        string folder = Folder(); string output = Path.Combine(folder, "grid");
        var pixels = Enumerable.Repeat((byte)255, 400 * 700 * 4).ToArray();
        async IAsyncEnumerable<RgbaFrame> frames() { for (int i = 0; i < 8; i++) { yield return new(400, 700, pixels); await Task.Yield(); } }
        await AtlasExporter.ExportAsync(frames(), output, new() { Layout = AtlasLayout.Grid, MaxAtlasSize = 2048, Deduplicate = true }, 24, expectedFrames: 8);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "atlas-000.json")));
        var data = json.RootElement.GetProperty("frames");
        Assert.Equal(8, data.EnumerateObject().Count());
        Assert.Equal(409, data.GetProperty("frame_000001").GetProperty("frame").GetProperty("x").GetInt32());
        Assert.Equal(3, data.GetProperty("frame_000001").GetProperty("frame").GetProperty("y").GetInt32());
        Assert.Equal(709, data.GetProperty("frame_000005").GetProperty("frame").GetProperty("y").GetInt32());
    }
    [Fact]
    public async Task CorruptVideoFailsWithReadableError()
    {
        string folder = Folder(); string path = Path.Combine(folder, "broken.mp4"); await File.WriteAllTextAsync(path, "not a movie");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new Ffmpeg().ProbeAsync(path)); Assert.NotEmpty(error.Message);
    }
    [Fact]
    public async Task WebmCancellationPreservesExistingOutputAndRemovesTemporaryFile()
    {
        string folder = Folder(); var ffmpeg = new Ffmpeg(); string source = await GenerateAsync(ffmpeg, folder, "libx264", "96x64");
        string output = Path.Combine(folder, "existing.webm"); await File.WriteAllTextAsync(output, "existing data");
        var info = await ffmpeg.ProbeAsync(source); var project = new ProjectDocument { SourcePath = source, TrimEnd = info.Duration };
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExportService(ffmpeg, () => new GpuFrameProcessor()).ExportWebmAsync(project, info, output, ct: cts.Token));
        Assert.Equal("existing data", await File.ReadAllTextAsync(output)); Assert.Empty(Directory.GetFiles(folder, "*.staging-*"));
    }
}
