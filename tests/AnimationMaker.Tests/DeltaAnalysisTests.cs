using System.Text.Json;
using AnimationMaker.Core;
using AnimationMaker.Gpu;
using Xunit;

namespace AnimationMaker.Tests;

public sealed class DeltaAnalysisTests
{
    private static string Folder() => Path.Combine(Path.GetTempPath(), "AnimationMaker-tests", "analysis-" + Guid.NewGuid().ToString("N"));
    private static async IAsyncEnumerable<RgbaFrame> Sequence(IEnumerable<RgbaFrame> frames)
    { foreach (var frame in frames) { yield return frame; await Task.Yield(); } }
    private static RgbaFrame RandomImage(int width, int height, int seed)
    {
        var bytes = new byte[width * height * 4]; new Random(seed).NextBytes(bytes);
        for (int i = 3; i < bytes.Length; i += 4) bytes[i] = new byte[] { 0, 1, 2, 7, 127, 128, 254, 255 }[(i / 4) % 8];
        return new(width, height, bytes);
    }
    private static void Apply(RgbaFrame canvas, RgbaFrame current, IReadOnlyList<DeltaOperation> operations, bool keyframe)
    {
        if (keyframe) Array.Clear(canvas.Pixels);
        foreach (var op in operations) for (int y = op.Y; y < op.Y + op.Height; y++)
            Buffer.BlockCopy(current.Pixels, (y * canvas.Width + op.X) * 4, canvas.Pixels, (y * canvas.Width + op.X) * 4, op.Width * 4);
    }
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(17, 31, 0)]
    [InlineData(97, 65, 8)]
    [InlineData(257, 193, 32)]
    [InlineData(1920, 1080, 8)]
    public void GpuMatchesCpuForMasksBoundsErrorsHiddenRgbAndPersistentReconstruction(int width, int height, int tolerance)
    {
        using var cpu = new CpuDeltaAnalyzer(); using var gpu = new GpuDeltaAnalyzer();
        var canvas = new RgbaFrame(width, height, new byte[width * height * 4]);
        var initial = RandomImage(width, height, 901);
        for (int n = 0; n < 7; n++)
        {
            var current = initial with { Pixels = (byte[])initial.Pixels.Clone() };
            var operations = new List<DeltaOperation>(); bool keyframe = n is 0 or 5;
            if (n is 0 or 5) operations.Add(new("replace", 0, 0, width, height, "p"));
            if (n is 1 or 2 or 6)
            {
                int w = Math.Min(7, width), h = Math.Min(3, height), x = width - w, y = height - h;
                for (int yy = y; yy < height; yy++) for (int xx = x; xx < width; xx++)
                {
                    int i = (yy * width + xx) * 4;
                    if (n == 2) Array.Clear(current.Pixels, i, 4);
                    else { current.Pixels[i] = (byte)(n * 31); current.Pixels[i + 3] = (byte)(n == 1 ? 1 : 128); }
                }
                operations.Add(new(n == 2 ? "clear" : "replace", x, y, w, h, n == 2 ? null : "p"));
            }
            if (n == 3) for (int i = 0; i < current.Pixels.Length; i += 4) current.Pixels[i] = (byte)Math.Min(255, current.Pixels[i] + 1);
            var expected = cpu.Analyze(current, canvas, tolerance); var actual = gpu.Analyze(current, canvas, tolerance);
            Assert.True(AdaptiveDeltaAnalyzer.Equivalent(expected, actual));
            Apply(canvas, current, operations, keyframe); gpu.Commit(current, operations, keyframe);
            Assert.Equal(cpu.MeasureError(current, canvas), gpu.MeasureError(current, canvas));
            initial = current;
        }
    }
    [Fact]
    public void ProductionAnalyzerUsesProcessedGpuTextureWithoutReuploadingCurrentRgba()
    {
        var profile = new ExportProfiler(); using var processor = new GpuFrameProcessor { Profiler = profile };
        using var gpu = processor.CreateDeltaAnalyzer(profile); using var cpu = new CpuDeltaAnalyzer();
        var canvas = new RgbaFrame(83, 57, new byte[83 * 57 * 4]);
        for (int n = 0; n < 3; n++)
        {
            var frame = processor.Process(RandomImage(83, 57, n), new() { Enabled = false }, [], n, new(0, 0, 83, 57), new(83, 57));
            Assert.True(AdaptiveDeltaAnalyzer.Equivalent(cpu.Analyze(frame, canvas, 8), gpu.Analyze(frame, canvas, 8)));
            DeltaOperation[] ops = [new("replace", 0, 0, 83, 57, "p")];
            Apply(canvas, frame, ops, true); gpu.Commit(frame, ops, true);
            Assert.Equal(new(0, 0), gpu.MeasureError(frame, canvas));
        }
        var snapshot = profile.Snapshot();
        Assert.True(snapshot.Counters["shared_gpu_texture_accesses"] > 0);
        Assert.False(snapshot.Counters.ContainsKey("gpu_source_upload_bytes"));
        Assert.Equal(3L * 83 * 57 * 4, snapshot.Counters["rgba_readback_bytes"]);
        Assert.True(snapshot.GpuMilliseconds["keyer"] > 0); Assert.True(snapshot.GpuMilliseconds["delta_kernel"] > 0);
    }
    [Theory]
    [InlineData(AtlasImageFormat.Png, 0)]
    [InlineData(AtlasImageFormat.Png, 8)]
    [InlineData(AtlasImageFormat.Webp, 32)]
    public async Task CpuAndGpuExportsHaveIdenticalManifestsAndTextureBytes(AtlasImageFormat format, int tolerance)
    {
        var seed = RandomImage(49, 35, 71);
        var frames = Enumerable.Range(0, 34).Select(n => {
            var f = seed with { Pixels = (byte[])seed.Pixels.Clone() };
            if (n is 19 or 20) Array.Clear(f.Pixels);
            else if (n > 2) for (int y = 10; y < 14; y++) for (int x = 4; x < 11; x++)
            { int i = (y * f.Width + x) * 4; f.Pixels[i] = (byte)(n * 5); f.Pixels[i + 3] = (byte)(n % 2 == 0 ? 1 : 128); }
            return f;
        }).ToArray();
        string cpuPath = Folder(), gpuPath = Folder();
        var settings = new ExportSettings { ImageFormat = format, DeltaTolerance = tolerance, DeltaAnalysisBackend = DeltaAnalysisBackend.Cpu };
        var cpuReport = await DeltaAtlasExporter.ExportAsync(Sequence(frames), cpuPath, settings, 29.97, expectedFrames: frames.Length);
        var profile = new ExportProfiler();
        using var analyzer = new AdaptiveDeltaAnalyzer(DeltaAnalysisBackend.Gpu, () => new GpuDeltaAnalyzer(profile), profile, frames.Length);
        var gpuReport = await DeltaAtlasExporter.ExportAsync(Sequence(frames), gpuPath, settings with { DeltaAnalysisBackend = DeltaAnalysisBackend.Gpu }, 29.97,
            expectedFrames: frames.Length, analyzer: analyzer, profiler: profile);
        Assert.Equal("GPU", gpuReport.Performance!.EffectiveBackend);
        Assert.Equal(cpuReport.DeltaBytes, gpuReport.DeltaBytes);
        foreach (string file in Directory.EnumerateFiles(cpuPath)) Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(gpuPath, Path.GetFileName(file))));
        Assert.Equal(cpuReport.MaximumVisibleError, gpuReport.MaximumVisibleError); Assert.Equal(cpuReport.MeanVisibleError, gpuReport.MeanVisibleError);
        Assert.True(gpuReport.Performance.Counters["gpu_analysis_readback_bytes"] > 0);
    }
    [Fact]
    public async Task BackendSelectionPersistsAndOldProjectsDefaultToAuto()
    {
        Assert.Equal(DeltaAnalysisBackend.Auto, JsonSerializer.Deserialize<ExportSettings>("{}", ProjectStore.JsonOptions)!.DeltaAnalysisBackend);
        string root = Folder(); Directory.CreateDirectory(root); string source = Path.Combine(root, "source.mp4"); File.WriteAllText(source, "fixture");
        foreach (var mode in Enum.GetValues<DeltaAnalysisBackend>())
        {
            string path = Path.Combine(root, "project.animproj");
            await ProjectStore.SaveAsync(path, new() { SourcePath = source, TrimEnd = 1, Export = new() { DeltaAnalysisBackend = mode } });
            Assert.Equal(mode, (await ProjectStore.LoadAsync(path)).Export.DeltaAnalysisBackend);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => DeltaAtlasExporter.ExportAsync(Sequence([RandomImage(8, 8, 0)]), Folder(), new() { DeltaAnalysisBackend = (DeltaAnalysisBackend)90 }, 24));
    }
    [Theory]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(30)]
    [InlineData(59)]
    public void ShortAutoClipsSkipCalibrationAndCancellationDoesNotInitializeGpu(int frameCount)
    {
        int calls = 0; var profile = new ExportProfiler();
        using var analyzer = new AdaptiveDeltaAnalyzer(DeltaAnalysisBackend.Auto, () => { calls++; throw new DeltaBackendUnavailableException("unavailable"); }, profile, frameCount);
        var image = RandomImage(9, 7, 42); analyzer.Analyze(image, image, 8); Assert.Equal(0, calls); Assert.Equal("CPU", profile.EffectiveBackend);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var forced = new AdaptiveDeltaAnalyzer(DeltaAnalysisBackend.Gpu, () => { calls++; throw new DeltaBackendUnavailableException("unavailable"); }, new(), 50);
        Assert.ThrowsAny<OperationCanceledException>(() => forced.Analyze(image, image, 8, cancelled.Token)); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellationDuringParallelLayoutEncodingRemovesAllStaging()
    {
        string path = Folder(); using var cts = new CancellationTokenSource();
        var progress = new CallbackProgress(p => { if (p.Stage == "Comparing compressed atlas layouts") cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeltaAtlasExporter.ExportAsync(
            Sequence(Enumerable.Range(0, 4).Select(n => RandomImage(48, 35, n))), path,
            new() { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 0 }, 24, progress, ct: cts.Token));
        Assert.False(Directory.Exists(path));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".staging-*"));
    }

    [Fact]
    public void AutoRejectsGpuCalibrationMismatchBeforeAnyCommit()
    {
        var profile = new ExportProfiler(); var image = RandomImage(9, 7, 42);
        using var analyzer = new AdaptiveDeltaAnalyzer(DeltaAnalysisBackend.Auto, () => new IncorrectAnalyzer(), profile, 120);
        var actual = analyzer.Analyze(image, image, 8);
        Assert.Null(actual.Bounds); Assert.Equal("CPU", analyzer.Backend); Assert.Contains("calibration results differ", profile.BackendReason!);
    }

    [Fact]
    public async Task ProductionServiceHonorsGpuModeAndReportsDecodeAndHardwareTimings()
    {
        string root = Folder(); Directory.CreateDirectory(root); string source = Path.Combine(root, "source.mp4"); var ffmpeg = new Ffmpeg();
        using (var generate = Ffmpeg.Start(ffmpeg.FfmpegPath, ["-v", "error", "-f", "lavfi", "-i", "color=green:s=96x64:r=12:d=1,drawbox=x=24:y=16:w=16:h=16:color=red:t=fill", "-c:v", "libx264", "-pix_fmt", "yuv420p", source]))
        { var errors = generate.StandardError.ReadToEndAsync(); await generate.WaitForExitAsync(); Assert.True(generate.ExitCode == 0, await errors); }
        var project = new ProjectDocument { SourcePath = source, TrimEnd = 1, Fps = 12, Export = new() { ImageFormat = AtlasImageFormat.Png, DeltaAnalysisBackend = DeltaAnalysisBackend.Gpu } };
        var service = new ExportService(ffmpeg, () => new GpuFrameProcessor());
        var report = await service.ExportDeltaAtlasAsync(project, await ffmpeg.ProbeAsync(source), Path.Combine(root, "export"));
        var p = report.Performance!;
        Assert.Equal("GPU", p.EffectiveBackend); Assert.True(p.WallMilliseconds["decode_wait"] > 0); Assert.True(p.GpuMilliseconds["keyer"] > 0);
        Assert.True(p.Counters["shared_gpu_texture_accesses"] > 0); Assert.False(p.Counters.ContainsKey("gpu_source_upload_bytes"));
        Assert.Equal(12, report.VerifiedFrames);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GpuFailureFallsBackToCpuAndPreservesValidReconstruction(bool failCommit)
    {
        var frames = Enumerable.Range(0, 4).Select(n => RandomImage(13, 17, n)).ToArray();
        var profile = new ExportProfiler();
        using var analyzer = new AdaptiveDeltaAnalyzer(DeltaAnalysisBackend.Gpu,
            () => failCommit ? new FailingCommitAnalyzer() : throw new DeltaBackendUnavailableException("unsupported test device"), profile, frames.Length);
        string cpu = Folder(), fallback = Folder(); var settings = new ExportSettings { ImageFormat = AtlasImageFormat.Png, DeltaTolerance = 0 };
        await DeltaAtlasExporter.ExportAsync(Sequence(frames), cpu, settings, 24);
        var report = await DeltaAtlasExporter.ExportAsync(Sequence(frames), fallback, settings, 24, analyzer: analyzer, profiler: profile);
        Assert.Equal("CPU", report.Performance!.EffectiveBackend); Assert.Contains("fallback", report.Performance.BackendReason!);
        foreach (string file in Directory.EnumerateFiles(cpu)) Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(fallback, Path.GetFileName(file))));
    }
    private sealed class FailingCommitAnalyzer : IDeltaAnalyzer
    {
        private readonly CpuDeltaAnalyzer _cpu = new();
        public string Backend => "GPU";
        public DeltaAnalysis Analyze(RgbaFrame current, RgbaFrame previous, int tolerance, CancellationToken ct = default) => _cpu.Analyze(current, previous, tolerance, ct);
        public DeltaError MeasureError(RgbaFrame current, RgbaFrame previous, CancellationToken ct = default) => throw new InvalidOperationException("Failed GPU must not be reused.");
        public void Commit(RgbaFrame current, IReadOnlyList<DeltaOperation> operations, bool keyframe, CancellationToken ct = default) => throw new DeltaBackendUnavailableException("test interrupted GPU commit");
        public void Dispose() => _cpu.Dispose();
    }
    private sealed class IncorrectAnalyzer : IDeltaAnalyzer
    {
        private readonly CpuDeltaAnalyzer _cpu = new();
        public string Backend => "GPU";
        public DeltaAnalysis Analyze(RgbaFrame current, RgbaFrame previous, int tolerance, CancellationToken ct = default)
            => _cpu.Analyze(current, previous, tolerance, ct) with { Error = new(255, 255) };
        public DeltaError MeasureError(RgbaFrame current, RgbaFrame reconstructed, CancellationToken ct = default) => throw new InvalidOperationException();
        public void Commit(RgbaFrame current, IReadOnlyList<DeltaOperation> operations, bool keyframe, CancellationToken ct = default) => throw new InvalidOperationException();
        public void Dispose() => _cpu.Dispose();
    }
    private sealed class CallbackProgress(Action<ExportProgress> callback) : IProgress<ExportProgress>
    { public void Report(ExportProgress value) => callback(value); }
}
