using System.Diagnostics;

namespace AnimationMaker.Core;

public sealed record DeltaError(int Maximum, long Sum);
public sealed record DeltaAnalysis(bool[] Mask, RectI? Bounds, bool[] DirtyTiles, RectI? NonzeroBounds, DeltaError Error);
public interface IDeltaAnalyzer : IDisposable
{
    string Backend { get; }
    DeltaAnalysis Analyze(RgbaFrame current, RgbaFrame previous, int tolerance, CancellationToken ct = default);
    void Commit(RgbaFrame current, IReadOnlyList<DeltaOperation> operations, bool keyframe, CancellationToken ct = default);
    DeltaError MeasureError(RgbaFrame current, RgbaFrame reconstructed, CancellationToken ct = default);
}
public sealed class DeltaBackendUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class CpuDeltaAnalyzer : IDeltaAnalyzer
{
    public string Backend => "CPU";
    public static int VisibleError(byte[] a, byte[] b, int i)
    {
        int alpha = a[i + 3], oldAlpha = b[i + 3], error = Math.Abs(alpha - oldAlpha) * 255;
        for (int c = 0; c < 3; c++)
        {
            error = Math.Max(error, Math.Abs(a[i + c] * alpha - b[i + c] * oldAlpha));
            error = Math.Max(error, Math.Abs((255 - a[i + c]) * alpha - (255 - b[i + c]) * oldAlpha));
        }
        return (error + 254) / 255;
    }
    public DeltaAnalysis Analyze(RgbaFrame current, RgbaFrame previous, int tolerance, CancellationToken ct = default)
    {
        Validate(current, previous, tolerance);
        int width = current.Width, height = current.Height, columns = (width + 15) / 16;
        var mask = new bool[width * height]; var tiles = new bool[columns * ((height + 15) / 16)];
        int l = width, t = height, r = 0, b = 0, nl = width, nt = height, nr = 0, nb = 0, maximum = 0; long sum = 0;
        for (int y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int pixel = y * width + x, i = pixel * 4;
                int error = VisibleError(current.Pixels, previous.Pixels, i); maximum = Math.Max(maximum, error); sum += error;
                if ((current.Pixels[i] | current.Pixels[i + 1] | current.Pixels[i + 2] | current.Pixels[i + 3]) != 0)
                { nl = Math.Min(nl, x); nt = Math.Min(nt, y); nr = Math.Max(nr, x + 1); nb = Math.Max(nb, y + 1); }
                bool changed = tolerance == 0 ? !previous.Pixels.AsSpan(i, 4).SequenceEqual(current.Pixels.AsSpan(i, 4)) :
                    error > tolerance || (previous.Pixels[i + 3] == 0) != (current.Pixels[i + 3] == 0);
                if (!changed) continue;
                mask[pixel] = true; tiles[y / 16 * columns + x / 16] = true;
                l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x + 1); b = Math.Max(b, y + 1);
            }
        }
        return new(mask, r == 0 ? null : new(l, t, r - l, b - t), tiles,
            nr == 0 ? null : new(nl, nt, nr - nl, nb - nt), new(maximum, sum));
    }
    public static void Validate(RgbaFrame current, RgbaFrame previous, int tolerance)
    {
        current.Validate(); previous.Validate();
        if (current.Width != previous.Width || current.Height != previous.Height || tolerance is < 0 or > 32) throw new InvalidDataException("Invalid Delta analysis input.");
    }
    public DeltaError MeasureError(RgbaFrame current, RgbaFrame reconstructed, CancellationToken ct = default)
    {
        Validate(current, reconstructed, 0); int maximum = 0; long sum = 0;
        for (int y = 0; y < current.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = y * current.Width * 4; i < (y + 1) * current.Width * 4; i += 4)
            { int e = VisibleError(current.Pixels, reconstructed.Pixels, i); maximum = Math.Max(maximum, e); sum += e; }
        }
        return new(maximum, sum);
    }
    public void Commit(RgbaFrame current, IReadOnlyList<DeltaOperation> operations, bool keyframe, CancellationToken ct = default) => ct.ThrowIfCancellationRequested();
    public void Dispose() { }
}

/// <summary>GPU failures always return to the authoritative CPU reconstruction owned by the exporter.</summary>
public sealed class AdaptiveDeltaAnalyzer : IDeltaAnalyzer
{
    private readonly CpuDeltaAnalyzer _cpu = new();
    private readonly DeltaAnalysisBackend _requested;
    private readonly Func<IDeltaAnalyzer>? _factory;
    private readonly ExportProfiler _profile;
    private readonly int _expectedFrames;
    private readonly Action<string>? _status;
    private IDeltaAnalyzer? _gpu;
    private bool _selected;
    public string Backend => _gpu?.Backend ?? "CPU";
    public AdaptiveDeltaAnalyzer(DeltaAnalysisBackend requested, Func<IDeltaAnalyzer>? factory, ExportProfiler profile,
        int expectedFrames, Action<string>? status = null)
    {
        if (!Enum.IsDefined(requested)) throw new InvalidDataException("Unsupported Delta analysis backend.");
        _requested = requested; _factory = factory; _profile = profile; _expectedFrames = expectedFrames; _status = status;
        _profile.RequestedBackend = requested.ToString();
        if (requested == DeltaAnalysisBackend.Cpu) SelectCpu("CPU selected");
    }
    private void SelectCpu(string reason)
    {
        _gpu?.Dispose(); _gpu = null; _selected = true;
        _profile.EffectiveBackend = "CPU"; _profile.BackendReason = reason;
        _status?.Invoke("Delta analysis · CPU · " + reason);
    }
    private void Select(RgbaFrame current, RgbaFrame previous, int tolerance, CancellationToken ct)
    {
        if (_selected) return;
        if (_factory == null) { SelectCpu("GPU analysis unavailable for this frame source"); return; }
        if (_requested == DeltaAnalysisBackend.Auto && _expectedFrames < 60) { SelectCpu("Short/unknown clip; calibration would not amortize"); return; }
        long started = Stopwatch.GetTimestamp();
        try
        {
            _gpu = _factory();
            if (_requested == DeltaAnalysisBackend.Gpu)
            {
                _selected = true; _profile.EffectiveBackend = _gpu.Backend; _profile.BackendReason = "GPU selected";
                _status?.Invoke("Delta analysis · GPU"); return;
            }
            // Calibration never commits either reconstruction. The first pass warms shaders/driver resources.
            var cpuReference = _cpu.Analyze(current, previous, tolerance, ct);
            var gpuReference = _gpu.Analyze(current, previous, tolerance, ct);
            if (!Equivalent(cpuReference, gpuReference)) throw new DeltaBackendUnavailableException("CPU/GPU calibration results differ");
            var cpu = new double[3]; var gpu = new double[3];
            for (int i = 0; i < 3; i++)
            {
                ct.ThrowIfCancellationRequested();
                long stamp = Stopwatch.GetTimestamp(); _cpu.Analyze(current, previous, tolerance, ct); cpu[i] = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
                stamp = Stopwatch.GetTimestamp(); _gpu.Analyze(current, previous, tolerance, ct); gpu[i] = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
            }
            Array.Sort(cpu); Array.Sort(gpu);
            double cost = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            double margin = Math.Max(cpu[2] - cpu[0], gpu[2] - gpu[0]);
            bool useful = cpu[1] - gpu[1] > Math.Max(cpu[1] * .15, margin) && (cpu[1] - gpu[1]) * Math.Max(0, _expectedFrames - 1) > cost;
            string reason = $"Auto sample: CPU {cpu[1]:0.###} ms / GPU {gpu[1]:0.###} ms incl. transfers";
            if (!useful) SelectCpu(reason + "; CPU preferred");
            else { _selected = true; _profile.EffectiveBackend = _gpu.Backend; _profile.BackendReason = reason; _status?.Invoke("Delta analysis · GPU · " + reason); }
        }
        catch (DeltaBackendUnavailableException ex) { SelectCpu("GPU fallback: " + ex.Message); }
        finally { if (_requested == DeltaAnalysisBackend.Auto) _profile.CalibrationMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }
    public static bool Equivalent(DeltaAnalysis a, DeltaAnalysis b) => a.Bounds == b.Bounds && a.NonzeroBounds == b.NonzeroBounds &&
        a.Error == b.Error && a.Mask.AsSpan().SequenceEqual(b.Mask) && a.DirtyTiles.AsSpan().SequenceEqual(b.DirtyTiles);
    public DeltaAnalysis Analyze(RgbaFrame current, RgbaFrame previous, int tolerance, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Select(current, previous, tolerance, ct);
        if (_gpu != null)
        {
            try { return _gpu.Analyze(current, previous, tolerance, ct); }
            catch (DeltaBackendUnavailableException ex) { SelectCpu("GPU fallback: " + ex.Message); }
        }
        return _cpu.Analyze(current, previous, tolerance, ct);
    }
    public void Commit(RgbaFrame current, IReadOnlyList<DeltaOperation> operations, bool keyframe, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_gpu == null) return;
        try { _gpu.Commit(current, operations, keyframe, ct); }
        catch (DeltaBackendUnavailableException ex) { SelectCpu("GPU fallback during reconstruction: " + ex.Message); }
    }
    public DeltaError MeasureError(RgbaFrame current, RgbaFrame reconstructed, CancellationToken ct = default)
    {
        if (_gpu != null)
        {
            try { return _gpu.MeasureError(current, reconstructed, ct); }
            catch (DeltaBackendUnavailableException ex) { SelectCpu("GPU fallback during verification: " + ex.Message); }
        }
        return _cpu.MeasureError(current, reconstructed, ct);
    }
    public void Dispose() { _gpu?.Dispose(); _gpu = null; _cpu.Dispose(); }
}
