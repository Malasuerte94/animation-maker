using System.Diagnostics;
using Vortice.Direct3D11;

namespace AnimationMaker.Gpu;

internal sealed class GpuTimestamp : IDisposable
{
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Query _disjoint, _start, _end;
    public GpuTimestamp(ID3D11Device device, ID3D11DeviceContext context)
    {
        _context = context;
        _disjoint = device.CreateQuery(new(QueryType.TimestampDisjoint));
        _start = device.CreateQuery(new(QueryType.Timestamp)); _end = device.CreateQuery(new(QueryType.Timestamp));
    }
    public void Begin() { _context.Begin(_disjoint); _context.End(_start); }
    public void End() { _context.End(_end); _context.End(_disjoint); }
    public double? Read(CancellationToken ct = default)
    {
        var state = Read<QueryDataTimestampDisjoint>(_disjoint, ct);
        ulong start = Read<ulong>(_start, ct), end = Read<ulong>(_end, ct);
        return state.Disjoint || state.Frequency == 0 ? null : (end - start) * 1000d / state.Frequency;
    }
    private unsafe T Read<T>(ID3D11Query query, CancellationToken ct) where T : unmanaged
    {
        T result = default; long start = Stopwatch.GetTimestamp(); var spin = new SpinWait();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var status = _context.GetData(query, (IntPtr)(&result), (uint)sizeof(T), AsyncGetDataFlags.DoNotFlush);
            status.CheckError(); if (status.Code == 0) return result;
            if (Stopwatch.GetElapsedTime(start).TotalSeconds > 2) throw new InvalidOperationException("GPU timestamp query timed out.");
            spin.SpinOnce();
        }
    }
    public void Dispose() { _end.Dispose(); _start.Dispose(); _disjoint.Dispose(); }
}
