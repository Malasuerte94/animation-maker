namespace AnimationMaker.Core;

public sealed record AnimationInfo(int Width, int Height, double Fps, IReadOnlyList<double> DurationsMs)
{
    public int FrameCount => DurationsMs.Count;
    public double Duration => DurationsMs.Sum() / 1000;
}

public interface IAnimationSource
{
    AnimationInfo Info { get; }
    RgbaFrame ReadFrame(int index, CancellationToken ct = default);
}
