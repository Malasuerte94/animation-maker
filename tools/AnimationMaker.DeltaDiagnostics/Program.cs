using System.Diagnostics;
using System.Text.Json;
using AnimationMaker.Core;
using AnimationMaker.Gpu;

if (args is ["--reencode", var input, var destination, var toleranceText])
{
    var source = new DeltaAtlasPlayer(input);
    int tolerance = int.Parse(toleranceText, System.Globalization.CultureInfo.InvariantCulture);
    async IAsyncEnumerable<RgbaFrame> decoded()
    {
        for (int i = 0; i < source.Info.FrameCount; i++) { yield return source.ReadFrame(i); await Task.Yield(); }
    }
    var timer = Stopwatch.StartNew();
    var report = await DeltaAtlasExporter.ExportAsync(decoded(), destination, new() { DeltaTolerance = tolerance }, source.Info.Fps,
        expectedFrames: source.Info.FrameCount, runtimeDirectory: Path.GetFullPath("runtime/pixi-delta/dist"));
    Console.WriteLine(JsonSerializer.Serialize(new { Seconds = timer.Elapsed.TotalSeconds, Report = report }, ProjectStore.JsonOptions));
    return;
}

string root = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/delta-validation"); Directory.CreateDirectory(root);
string runtime = Path.GetFullPath("runtime/pixi-delta/dist");
foreach (var (name, width, height, count) in new[] { ("sparse", 97, 65, 70), ("benchmark", 1920, 1080, 60), ("empty", 13, 9, 35) })
{
    string output = Path.Combine(root, name);
    if (Directory.Exists(output)) throw new IOException("Diagnostics require a new output path: " + output);
    var generated = new List<RgbaFrame>();
    async IAsyncEnumerable<RgbaFrame> sequence()
    {
        var baseImage = new byte[width * height * 4]; new Random(123).NextBytes(baseImage);
        for (int i = 3; i < baseImage.Length; i += 4) baseImage[i] = 255;
        byte[]? previous = null;
        for (int n = 0; n < count; n++)
        {
            byte[] pixels = name == "empty" || n == 0 || n == 38 ? new byte[baseImage.Length] : (byte[])baseImage.Clone();
            if (name != "empty" && n != 0 && n != 38)
                for (int y = 2; y < Math.Min(height, 21); y++) for (int x = 2; x < Math.Min(width, 43); x++)
                {
                    int i = (y * width + x) * 4; pixels[i] = (byte)(n + x); pixels[i + 1] = 35; pixels[i + 2] = 230;
                    pixels[i + 3] = new byte[] { 0, 1, 2, 7, 31, 127, 128, 254, 255 }[(x + n / 3) % 9];
                    if (pixels[i + 3] == 0) { pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 0; }
                }
            if (name == "sparse" && n > 0 && n != 38)
            {
                for (int y = height - 5; y < height; y++) for (int x = width - 5; x < width; x++)
                { int i = (y * width + x) * 4; pixels[i] = 240; pixels[i + 1] = (byte)n; pixels[i + 2] = 20; pixels[i + 3] = 7; }
                if (n % 11 == 5 && previous != null)
                {
                    pixels = (byte[])previous.Clone();
                    for (int y = height - 5; y < height; y++) Array.Clear(pixels, (y * width + width - 5) * 4, 5 * 4);
                }
                if (n % 17 == 0 && previous != null) pixels = (byte[])previous.Clone();
            }
            previous = pixels;
            var frame = new RgbaFrame(width, height, pixels); if (name == "sparse") generated.Add(frame);
            yield return frame; await Task.Yield();
        }
    }
    var timer = Stopwatch.StartNew();
    var report = await DeltaAtlasExporter.ExportAsync(sequence(), output, new() { MaxFrameHeight = height, DeltaTolerance = 0, ImageFormat = AtlasImageFormat.Png }, 30, expectedFrames: count, runtimeDirectory: runtime);
    Console.WriteLine($"{name}: {report.SavingsPercent:0.0}% smaller, {report.VerifiedFrames} frames verified, export {timer.Elapsed.TotalSeconds:0.0}s");
    if (name == "sparse")
    {
        string references = Path.Combine(output, "references"); Directory.CreateDirectory(references);
        for (int i = 0; i < generated.Count; i++) Imaging.SavePng(Path.Combine(references, $"{i:D3}.png"), generated[i]);
    }
    if (name == "benchmark")
    {
        var player = new DeltaAtlasPlayer(Path.Combine(output, "delta.animation.json")); using var gpu = new GpuFrameProcessor();
        var times = new List<double>();
        for (int i = 0; i < 120; i++)
        {
            var watch = Stopwatch.StartNew(); var frame = player.ReadFrame(i % count);
            gpu.Render(frame, new() { Enabled = false }, [], 0, new(0, 0, width, height), new(width, height)); _ = gpu.Readback();
            if (i >= 30) times.Add(watch.Elapsed.TotalMilliseconds);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "native-benchmark.json"), JsonSerializer.Serialize(new { gpu.AdapterName, Includes = "CPU delta reconstruction + GPU upload/render/readback, no codecs", MedianMs = times.Order().ElementAt(times.Count / 2), MeanMs = times.Average(), FrameBudget30FpsMs = 1000d / 30 }, ProjectStore.JsonOptions));
    }
}
