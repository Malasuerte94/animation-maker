using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using AnimationMaker.Core;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace AnimationMaker.Gpu;

/// <summary>Integer-equivalent RGBA8 analysis. Production shares the processor's final texture and D3D context.</summary>
public sealed class GpuDeltaAnalyzer : IDeltaAnalyzer
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly object _gate;
    private readonly bool _ownsDevice;
    private readonly Func<RgbaFrame, ID3D11Texture2D?>? _processedTexture;
    private readonly ExportProfiler? _profile;
    private ID3D11ComputeShader? _shader;
    private ID3D11Buffer? _constants, _results, _readback;
    private ID3D11UnorderedAccessView? _uav;
    private ID3D11Texture2D? _canvas, _uploaded;
    private ID3D11ShaderResourceView? _canvasView;
    private ID3D11RenderTargetView? _canvasTarget;
    private GpuTimestamp? _timer;
    private int _width, _height, _columns, _rows;
    private bool _initialized, _disposed;
    private byte[]? _lastPixels;
    private ID3D11Texture2D? _lastTexture;
    public string Backend => "GPU";
    public GpuDeltaAnalyzer(ExportProfiler? profiler = null)
    {
        _gate = new(); _profile = profiler; _ownsDevice = true;
        try { D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out _device, out _context).CheckError(); }
        catch (SharpGenException ex) { throw new DeltaBackendUnavailableException("Direct3D device unavailable: " + ex.Message, ex); }
        Initialize();
    }
    internal GpuDeltaAnalyzer(ID3D11Device device, ID3D11DeviceContext context, object gate,
        Func<RgbaFrame, ID3D11Texture2D?> processedTexture, ExportProfiler profiler)
    {
        _device = device; _context = context; _gate = gate; _processedTexture = processedTexture; _profile = profiler;
        Initialize();
    }
    private void Initialize()
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            using var stream = typeof(GpuDeltaAnalyzer).Assembly.GetManifestResourceStream("AnimationMaker.Gpu.Shaders.DeltaAnalysis.hlsl")!;
            using var reader = new StreamReader(stream);
            var code = Compiler.Compile(reader.ReadToEnd(), "Analyze", "DeltaAnalysis.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3);
            _shader = _device.CreateComputeShader(code.ToArray());
            _constants = _device.CreateBuffer(16, BindFlags.ConstantBuffer);
            if (_profile != null) _timer = new(_device, _context);
        }
        catch (Exception ex) when (ex is SharpGenException or InvalidOperationException or NotSupportedException)
        { Dispose(); throw new DeltaBackendUnavailableException("DirectCompute initialization failed: " + ex.Message, ex); }
        finally { _profile?.AddWall("gpu_analysis_initialization", Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Settings { public uint Width, Height, Tolerance, Columns; }
    private void Ensure(RgbaFrame current, RgbaFrame previous)
    {
        if (_disposed) throw new InvalidOperationException("GPU analyzer is disposed.");
        if (_width == current.Width && _height == current.Height) return;
        ReleaseImages(); _width = current.Width; _height = current.Height; _columns = (_width + 15) / 16; _rows = (_height + 15) / 16;
        _canvas = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)_width, (uint)_height, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget));
        _canvasView = _device.CreateShaderResourceView(_canvas); _canvasTarget = _device.CreateRenderTargetView(_canvas);
        uint bytes = checked((uint)(_columns * _rows * 20 * 4));
        _results = _device.CreateBuffer(new BufferDescription { ByteWidth = bytes, BindFlags = BindFlags.UnorderedAccess,
            MiscFlags = ResourceOptionFlags.BufferStructured, StructureByteStride = 4, Usage = ResourceUsage.Default });
        _uav = _device.CreateUnorderedAccessView(_results);
        _readback = _device.CreateBuffer(new BufferDescription { ByteWidth = bytes, Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read });
        _context.UpdateSubresource(previous.Pixels, _canvas, 0, (uint)(_width * 4));
        _profile?.Count("gpu_state_upload_bytes", previous.Pixels.Length); _initialized = true;
    }
    private ID3D11Texture2D Current(RgbaFrame frame)
    {
        var shared = _processedTexture?.Invoke(frame);
        if (shared != null) { _profile?.Count("shared_gpu_texture_accesses"); _lastPixels = frame.Pixels; _lastTexture = shared; return shared; }
        if (ReferenceEquals(_lastPixels, frame.Pixels) && _lastTexture != null) return _lastTexture;
        _uploaded ??= _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)_width, (uint)_height, 1, 1, BindFlags.ShaderResource));
        _context.UpdateSubresource(frame.Pixels, _uploaded, 0, (uint)(_width * 4));
        _profile?.Count("gpu_source_upload_bytes", frame.Pixels.Length);
        _lastPixels = frame.Pixels; return _lastTexture = _uploaded;
    }
    private unsafe uint[] Dispatch(RgbaFrame current, RgbaFrame previous, int tolerance, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CpuDeltaAnalyzer.Validate(current, previous, tolerance); Ensure(current, previous);
        var texture = Current(current);
        using var view = _device.CreateShaderResourceView(texture);
        var settings = new Settings { Width = (uint)_width, Height = (uint)_height, Tolerance = (uint)tolerance, Columns = (uint)_columns };
        _context.UpdateSubresource(in settings, _constants!);
        _context.CSSetShader(_shader); _context.CSSetConstantBuffer(0, _constants!);
        _context.CSSetShaderResources(0, new[] { view, _canvasView! });
        _context.CSSetUnorderedAccessView(0, _uav!);
        _timer?.Begin();
        _context.Dispatch((uint)_columns, (uint)_rows, 1); _timer?.End();
        _context.CSSetUnorderedAccessView(0, null); _context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
        _context.CSSetShader(null);
        _context.CopyResource(_readback!, _results!);
        long readStart = Stopwatch.GetTimestamp();
        var mapped = _context.Map(_readback!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var result = new uint[_columns * _rows * 20];
        try { new ReadOnlySpan<uint>((void*)mapped.DataPointer, result.Length).CopyTo(result); }
        finally { _context.Unmap(_readback!, 0); }
        _profile?.AddWall("gpu_analysis_readback", Stopwatch.GetElapsedTime(readStart).TotalMilliseconds);
        _profile?.Count("gpu_analysis_readback_bytes", result.Length * 4L);
        if (_timer?.Read(ct) is { } milliseconds) _profile?.AddGpu("delta_kernel", milliseconds);
        ct.ThrowIfCancellationRequested(); return result;
    }
    public DeltaAnalysis Analyze(RgbaFrame current, RgbaFrame previous, int tolerance, CancellationToken ct = default)
    {
        lock (_gate) return Guard(() =>
        {
            ct.ThrowIfCancellationRequested(); var values = Dispatch(current, previous, tolerance, ct);
            var mask = new bool[_width * _height]; var tiles = new bool[_columns * _rows];
            int l = _width, t = _height, r = 0, b = 0, nl = _width, nt = _height, nr = 0, nb = 0, max = 0; long sum = 0;
            for (int tile = 0; tile < tiles.Length; tile++)
            {
                int offset = tile * 20;
                l = Math.Min(l, (int)values[offset + 8]); t = Math.Min(t, (int)values[offset + 9]);
                r = Math.Max(r, (int)values[offset + 10]); b = Math.Max(b, (int)values[offset + 11]);
                nl = Math.Min(nl, (int)values[offset + 14]); nt = Math.Min(nt, (int)values[offset + 15]);
                nr = Math.Max(nr, (int)values[offset + 16]); nb = Math.Max(nb, (int)values[offset + 17]);
                max = Math.Max(max, (int)values[offset + 12]); sum += values[offset + 13];
                int x = tile % _columns * 16, y = tile / _columns * 16;
                for (int word = 0; word < 8; word++)
                {
                    uint bits = values[offset + word]; if (bits != 0) tiles[tile] = true;
                    while (bits != 0)
                    {
                        int local = word * 32 + BitOperations.TrailingZeroCount(bits);
                        mask[(y + local / 16) * _width + x + local % 16] = true; bits &= bits - 1;
                    }
                }
            }
            return new DeltaAnalysis(mask, r == 0 ? null : new(l, t, r - l, b - t), tiles,
                nr == 0 ? null : new(nl, nt, nr - nl, nb - nt), new(max, sum));
        });
    }
    public void Commit(RgbaFrame current, IReadOnlyList<DeltaOperation> operations, bool keyframe, CancellationToken ct = default)
    {
        lock (_gate) Guard(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!_initialized) throw new InvalidOperationException("Analyze must initialize the GPU reconstruction before commit.");
            var texture = Current(current);
            if (keyframe) _context.ClearRenderTargetView(_canvasTarget!, new Color4(0, 0, 0, 0));
            foreach (var op in operations)
            {
                FrameGeometry.ValidateCrop(new(op.X, op.Y, op.Width, op.Height), _width, _height);
                _context.CopySubresourceRegion(_canvas!, 0, (uint)op.X, (uint)op.Y, 0, texture, 0,
                    new Box(op.X, op.Y, 0, op.X + op.Width, op.Y + op.Height, 1));
            }
            return true;
        });
    }
    public DeltaError MeasureError(RgbaFrame current, RgbaFrame reconstructed, CancellationToken ct = default)
    {
        lock (_gate) return Guard(() =>
        {
            var values = Dispatch(current, reconstructed, 0, ct); int maximum = 0; long sum = 0;
            for (int i = 0; i < values.Length; i += 20) { maximum = Math.Max(maximum, (int)values[i + 12]); sum += values[i + 13]; }
            return new DeltaError(maximum, sum);
        });
    }
    private static T Guard<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (Exception ex) when (ex is SharpGenException or InvalidOperationException or NotSupportedException)
        { throw new DeltaBackendUnavailableException("DirectCompute failed: " + ex.Message, ex); }
    }
    private void ReleaseImages()
    {
        _canvasView?.Dispose(); _canvasView = null; _canvasTarget?.Dispose(); _canvasTarget = null;
        _canvas?.Dispose(); _canvas = null; _uploaded?.Dispose(); _uploaded = null;
        _uav?.Dispose(); _uav = null; _results?.Dispose(); _results = null; _readback?.Dispose(); _readback = null;
        _lastTexture = null; _lastPixels = null; _initialized = false;
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            ReleaseImages(); _timer?.Dispose(); _shader?.Dispose(); _constants?.Dispose();
            if (_ownsDevice) { _context.Dispose(); _device.Dispose(); }
        }
    }
}
