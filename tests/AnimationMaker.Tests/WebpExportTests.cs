using System.Security.Cryptography;
using System.Text.Json;
using AnimationMaker.Core;
using Xunit;

namespace AnimationMaker.Tests;

public sealed class WebpExportTests
{
    private static string Folder() => Path.Combine(Path.GetTempPath(), "AnimationMaker-tests", "webp-" + Guid.NewGuid().ToString("N"));
    private static async IAsyncEnumerable<RgbaFrame> Sequence(IEnumerable<RgbaFrame> frames)
    { foreach (var frame in frames) { yield return frame; await Task.Yield(); } }
    private static RgbaFrame Image()
    {
        var pixels = new byte[96 * 72 * 4]; var random = new Random(78);
        for (int y = 6; y < 66; y++) for (int x = 6; x < 90; x++)
        {
            int i = (y * 96 + x) * 4;
            pixels[i] = (byte)random.Next(256); pixels[i + 1] = (byte)(x * 2); pixels[i + 2] = (byte)(y * 3);
            pixels[i + 3] = (byte)(1 + (x + y) % 255);
        }
        return new(96, 72, pixels);
    }

    [Fact]
    public async Task PackedAtlasAvoidsLongStripsAndPreservesFramePositions()
    {
        var frames = Enumerable.Range(0, 30).Select(n => {
            var frame = new RgbaFrame(64, 96, new byte[64 * 96 * 4]);
            for (int y = 10; y < 80; y++) for (int x = 8; x < 28; x++)
            {
                int i = (y * 64 + x) * 4; frame.Pixels[i] = (byte)(n * 7); frame.Pixels[i + 3] = 255;
            }
            return frame;
        }).ToArray();
        string path = Folder();
        await AtlasExporter.ExportAsync(Sequence(frames), path, new() { ImageFormat = AtlasImageFormat.Png }, 30);
        using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(path, "atlas-000.json")));
        var size = descriptor.RootElement.GetProperty("meta").GetProperty("size");
        int w = size.GetProperty("w").GetInt32(), h = size.GetProperty("h").GetInt32();
        Assert.InRange((double)Math.Max(w, h) / Math.Min(w, h), 1, 2.5);
        Assert.True((long)w * h < 30L * 26 * 76 * 1.3);
        var player = new AtlasPlayer(Path.Combine(path, "animation.json"));
        for (int i = 0; i < frames.Length; i++) Assert.Equal(frames[i].Pixels, player.ReadFrame(i).Pixels);
    }

    [Fact]
    public void WebpQualityChangesCompressionAndPreservesAlpha()
    {
        var frame = Image(); string path = Folder(); Directory.CreateDirectory(path);
        byte[] small = Imaging.EncodeWebp(frame, 25), high = Imaging.EncodeWebp(frame, 95);
        Assert.True(small.Length < high.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(small, 0, 4));
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(small, 8, 4));
        File.WriteAllBytes(Path.Combine(path, "image.webp"), small);
        var decoded = Imaging.Load(Path.Combine(path, "image.webp"));
        Assert.Equal(frame.Width, decoded.Width); Assert.Equal(frame.Height, decoded.Height);
        for (int i = 3; i < frame.Pixels.Length; i += 4) Assert.Equal(frame.Pixels[i], decoded.Pixels[i]);
    }

    [Theory]
    [InlineData(AtlasLayout.Packed)]
    [InlineData(AtlasLayout.Grid)]
    public async Task ClassicAtlasUsesSelectedWebpQualityAndJsonReferences(AtlasLayout layout)
    {
        var frame = Image(); var frames = new[] { frame, frame }; var sizes = new List<long>();
        foreach (int quality in new[] { 25, 95 })
        {
            string path = Folder();
            await AtlasExporter.ExportAsync(Sequence(frames), path, new() { Layout = layout, WebpQuality = quality }, 24, expectedFrames: 2);
            Assert.Empty(Directory.GetFiles(path, "*.png"));
            sizes.Add(Directory.GetFiles(path, "*.webp").Sum(f => new FileInfo(f).Length));
            using var atlas = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "atlas-000.json")));
            using var phaser = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "phaser.json")));
            Assert.Equal("atlas-000.webp", atlas.RootElement.GetProperty("meta").GetProperty("image").GetString());
            Assert.Equal("atlas-000.webp", phaser.RootElement.GetProperty("textures")[0].GetProperty("image").GetString());
            var player = new AtlasPlayer(Path.Combine(path, "animation.json"));
            for (int n = 0; n < 2; n++)
            {
                var decoded = player.ReadFrame(n);
                Assert.Equal(frame.Width, decoded.Width); Assert.Equal(frame.Height, decoded.Height);
                for (int i = 3; i < frame.Pixels.Length; i += 4) Assert.Equal(frame.Pixels[i], decoded.Pixels[i]);
            }
        }
        Assert.True(sizes[0] < sizes[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public async Task DeltaWebpVerifiesDecodedHashesSeeksClearsAndFinalVisibleError(int tolerance)
    {
        var initial = Image(); var frames = new List<RgbaFrame>();
        for (int n = 0; n < 42; n++)
        {
            var frame = initial with { Pixels = (byte[])initial.Pixels.Clone() };
            if (n is >= 33 and <= 35) Array.Clear(frame.Pixels);
            else if (n > 2)
                for (int y = 10; y < 14; y++) for (int x = 10; x < 15; x++) frame.Pixels[(y * frame.Width + x) * 4] = (byte)(n * 5);
            frames.Add(frame);
        }
        string path = Folder();
        var report = await DeltaAtlasExporter.ExportAsync(Sequence(frames), path, new() { DeltaTolerance = tolerance }, 24, expectedFrames: frames.Count);
        var player = new DeltaAtlasPlayer(Path.Combine(path, "delta.animation.json"));
        Assert.Equal(new("lossy", tolerance, "webp", 75), player.Manifest.Encoding);
        Assert.Equal("webp", report.TextureFormat); Assert.Equal(75, report.TextureQuality);
        Assert.All(player.Manifest.Pages, p => Assert.EndsWith(".webp", p.Image));
        Assert.Empty(Directory.GetFiles(path, "*.png"));
        Assert.All(Directory.GetFiles(path), f => Assert.True(f.EndsWith(".webp", StringComparison.Ordinal) || Path.GetFileName(f) == "delta.animation.json"));
        Assert.Equal(report.DeltaBytes, new FileInfo(Path.Combine(path, "delta.animation.json")).Length + Directory.GetFiles(path, "*.webp").Sum(f => new FileInfo(f).Length));
        long sum = 0; int max = 0; var hashes = new List<string>();
        for (int n = 0; n < frames.Count; n++)
        {
            var decoded = player.ReadFrame(n);
            hashes.Add(Convert.ToHexString(SHA256.HashData(decoded.Pixels)));
            for (int i = 0; i < decoded.Pixels.Length; i += 4)
            {
                Assert.InRange(Math.Abs(frames[n].Pixels[i + 3] - decoded.Pixels[i + 3]), 0, tolerance);
                int error = VisibleError(frames[n].Pixels, decoded.Pixels, i); max = Math.Max(max, error); sum += error;
            }
        }
        Assert.Equal(max, report.MaximumVisibleError);
        Assert.Equal((double)sum / (frames.Count * initial.Width * initial.Height), report.MeanVisibleError, 8);
        Assert.True(report.MaximumVisibleError > tolerance);
        foreach (int n in new[] { 41, 0, 30, 34, 33, 2, 36 })
            Assert.Equal(hashes[n], Convert.ToHexString(SHA256.HashData(player.ReadFrame(n).Pixels)));
        Assert.All(player.ReadFrame(34).Pixels, b => Assert.Equal((byte)0, b));
    }

    private static int VisibleError(byte[] a, byte[] b, int i)
    {
        double error = Math.Abs(a[i + 3] - b[i + 3]);
        foreach (int background in new[] { 0, 255 }) for (int c = 0; c < 3; c++)
        {
            double actual = (a[i + c] * a[i + 3] + background * (255 - a[i + 3])) / 255d;
            double decoded = (b[i + c] * b[i + 3] + background * (255 - b[i + 3])) / 255d;
            error = Math.Max(error, Math.Abs(actual - decoded));
        }
        return (int)Math.Ceiling(error - 1e-9);
    }

    [Fact]
    public async Task ProjectSavesFormatAndQualityAndDefaultsMissingSettingsToMediumWebp()
    {
        string folder = Folder(); Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "source.mp4"); await File.WriteAllTextAsync(source, "fixture");
        string path = Path.Combine(folder, "project.animproj");
        var project = new ProjectDocument { SourcePath = source, TrimEnd = 1, Export = new() { ImageFormat = AtlasImageFormat.Png, WebpQuality = 42 } };
        await ProjectStore.SaveAsync(path, project);
        Assert.Equal(project.Export, (await ProjectStore.LoadAsync(path)).Export);
        var defaults = JsonSerializer.Deserialize<ExportSettings>("{}", ProjectStore.JsonOptions)!;
        Assert.Equal(AtlasImageFormat.Webp, defaults.ImageFormat); Assert.Equal(75, defaults.WebpQuality);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task InvalidQualityDoesNotCreateAnExport(int quality)
    {
        var settings = new ExportSettings { WebpQuality = quality }; string path = Folder();
        await Assert.ThrowsAsync<InvalidDataException>(() => AtlasExporter.ExportAsync(Sequence([Image()]), path, settings, 30));
        await Assert.ThrowsAsync<InvalidDataException>(() => DeltaAtlasExporter.ExportAsync(Sequence([Image()]), path, settings, 30));
        Assert.False(Directory.Exists(path));
    }
}
