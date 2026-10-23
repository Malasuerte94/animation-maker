using System.Diagnostics;

namespace AnimationMaker.Core;

public sealed record ExportPerformance(double TotalMilliseconds, Dictionary<string, double> WallMilliseconds,
    Dictionary<string, double> GpuMilliseconds, Dictionary<string, long> Counters, long PeakProcessBytes,
    string RequestedBackend, string EffectiveBackend, string? BackendReason, double CalibrationMilliseconds);

/// <summary>Wall stages can overlap; GPU timestamps are reported separately from CPU submission time.</summary>
public sealed class ExportProfiler
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly object _gate = new();
    private readonly Dictionary<string, double> _wall = [], _gpu = [];
    private readonly Dictionary<string, long> _counters = [];
    public string RequestedBackend { get; set; } = "CPU";
    public string EffectiveBackend { get; set; } = "CPU";
    public string? BackendReason { get; set; }
    public double CalibrationMilliseconds { get; set; }
    public readonly struct Measurement(ExportProfiler owner, string stage) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public void Dispose() => owner.AddWall(stage, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
    }
    public Measurement Measure(string stage) => new(this, stage);
    public void AddWall(string stage, double milliseconds) { lock (_gate) _wall[stage] = _wall.GetValueOrDefault(stage) + milliseconds; }
    public void AddGpu(string stage, double milliseconds) { lock (_gate) _gpu[stage] = _gpu.GetValueOrDefault(stage) + milliseconds; }
    public void Count(string name, long amount = 1) { lock (_gate) _counters[name] = _counters.GetValueOrDefault(name) + amount; }
    public ExportPerformance Snapshot()
    {
        using var process = Process.GetCurrentProcess();
        lock (_gate) return new(Stopwatch.GetElapsedTime(_started).TotalMilliseconds, new(_wall), new(_gpu), new(_counters),
            process.PeakWorkingSet64, RequestedBackend, EffectiveBackend, BackendReason, CalibrationMilliseconds);
    }
}

public interface IProfiledFrameProcessor { ExportProfiler? Profiler { get; set; } }
public interface IDeltaAnalyzerProvider { IDeltaAnalyzer CreateDeltaAnalyzer(ExportProfiler profiler); }
