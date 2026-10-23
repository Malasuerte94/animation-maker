using System.Diagnostics;
using System.Text.Json;
using AnimationMaker.Core;
using AnimationMaker.Gpu;

if (args is ["--benchmark-analysis", var benchmarkOutput])
{
    if (File.Exists(benchmarkOutput)) throw new IOException("Choose a new benchmark output path.");
    var measurements = new List<object>();
    foreach (var (name, width, height, dense) in new[] { ("small-sparse", 277, 512, false), ("1080p-sparse", 1920, 1080, false), ("1080p-dense", 1920, 1080, true), ("4k-sparse", 3840, 2160, false) })
        foreach (var mode in Enum.GetValues<DeltaAnalysisBackend>())
            for (int run = 0; run < 3; run++)
            {
                var profile = new ExportProfiler(); using var processor = new GpuFrameProcessor { Profiler = profile };
                using var analyzer = new AdaptiveDeltaAnalyzer(mode, () => processor.CreateDeltaAnalyzer(profile), profile, 64);
                var pixels = new byte[width * height * 4]; new Random(41).NextBytes(pixels);
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                var canvas = new RgbaFrame(width, height, new byte[pixels.Length]); var samples = new List<double>();
                for (int n = 0; n < 64; n++)
                {
                    var sampleInput = new RgbaFrame(width, height, (byte[])pixels.Clone());
                    if (dense) for (int i = 0; i < sampleInput.Pixels.Length; i += 4) sampleInput.Pixels[i] = (byte)(sampleInput.Pixels[i] + n * 17);
                    else for (int y = 4; y < 20; y++) for (int x = 4 + n; x < 20 + n; x++) sampleInput.Pixels[(y * width + x) * 4] = (byte)(n * 3);
                    var current = processor.Process(sampleInput, new() { Enabled = false }, [], n / 30d, new(0, 0, width, height), new(width, height));
                    var timer = Stopwatch.StartNew(); var analysis = analyzer.Analyze(current, canvas, 8);
                    bool key = n % 30 == 0;
                    RectI? bounds = key ? new(0, 0, width, height) : analysis.Bounds;
                    List<DeltaOperation> ops = bounds is { } r ? [new("replace", r.X, r.Y, r.Width, r.Height, "p")] : [];
                    if (key) Array.Clear(canvas.Pixels);
                    foreach (var op in ops) for (int y = op.Y; y < op.Y + op.Height; y++)
                        Buffer.BlockCopy(current.Pixels, (y * width + op.X) * 4, canvas.Pixels, (y * width + op.X) * 4, op.Width * 4);
                    analyzer.Commit(current, ops, key);
                    if (analyzer.MeasureError(current, canvas).Maximum > 8) throw new InvalidDataException("Analysis benchmark reconstruction failed.");
                    if (n >= 4) samples.Add(timer.Elapsed.TotalMilliseconds);
                }
                samples.Sort(); var performance = profile.Snapshot();
                measurements.Add(new { name, width, height, mode, run, actual = performance.EffectiveBackend,
                    medianAnalysisMs = samples[samples.Count / 2], meanAnalysisMs = samples.Average(), performance });
                Console.WriteLine($"{name} {mode} run {run + 1}: {samples[samples.Count / 2]:0.###} ms ({performance.EffectiveBackend})");
            }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(benchmarkOutput))!);
    await File.WriteAllTextAsync(benchmarkOutput, JsonSerializer.Serialize(new { Includes = "Delta scan + CPU mirror update + commit + error check; excludes keyer and image codecs; median excludes first four frames", measurements }, ProjectStore.JsonOptions));
    return;
}

if (args is ["--profile-video", var videoInput, var videoDestination, var videoBackend, var secondsText])
{
    var ffmpeg = new Ffmpeg(); var info = await ffmpeg.ProbeAsync(videoInput);
    double seconds = Math.Min(info.Duration, double.Parse(secondsText, System.Globalization.CultureInfo.InvariantCulture));
    var project = new ProjectDocument { SourcePath = Path.GetFullPath(videoInput), TrimEnd = seconds, Fps = Math.Min(30, info.Fps),
        Export = new() { DeltaAnalysisBackend = Enum.Parse<DeltaAnalysisBackend>(videoBackend, true) } };
    var service = new ExportService(ffmpeg, () => new GpuFrameProcessor());
    var report = await service.ExportDeltaAtlasAsync(project, info, videoDestination, runtimeDirectory: Path.GetFullPath("runtime/pixi-delta/dist"));
    Console.WriteLine(JsonSerializer.Serialize(report, ProjectStore.JsonOptions));
    return;
}

if (args is ["--profile", var profileInput, var profileDestination, var backendText, var limitText])
{
    var source = new DeltaAtlasPlayer(profileInput);
    int count = Math.Min(source.Info.FrameCount, int.Parse(limitText, System.Globalization.CultureInfo.InvariantCulture));
    var mode = Enum.Parse<DeltaAnalysisBackend>(backendText, true);
    var profiler = new ExportProfiler();
    using var analyzer = new AdaptiveDeltaAnalyzer(mode, () => new GpuDeltaAnalyzer(profiler), profiler, count);
    async IAsyncEnumerable<RgbaFrame> decoded()
    {
        for (int i = 0; i < count; i++) { yield return source.ReadFrame(i); await Task.Yield(); }
    }
    var report = await DeltaAtlasExporter.ExportAsync(decoded(), profileDestination, new() { DeltaAnalysisBackend = mode }, source.Info.Fps,
        expectedFrames: count, runtimeDirectory: Path.GetFullPath("runtime/pixi-delta/dist"), analyzer: analyzer, profiler: profiler);
    Console.WriteLine(JsonSerializer.Serialize(report, ProjectStore.JsonOptions));
    return;
}

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
