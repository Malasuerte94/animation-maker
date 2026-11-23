namespace AnimationMaker.Core;

// Scale is physical display pixels per output pixel. A null scale follows the viewport size.
// Pointer positions and pan offsets are in device-independent pixels.
public readonly record struct PreviewCamera(double? Scale, PointD Pan)
{
    public PreviewCamera ZoomAt(PointD pointer, PointD center, double fitScale, int wheelDelta)
    {
        double oldScale = Scale ?? fitScale;
        double newScale = Math.Clamp(oldScale * Math.Pow(1.2, wheelDelta / 120d),
            Math.Min(.05, fitScale / 4), Math.Max(32, fitScale * 4));
        double ratio = newScale / oldScale;
        return new(newScale, new(pointer.X - center.X - (pointer.X - center.X - Pan.X) * ratio,
                                 pointer.Y - center.Y - (pointer.Y - center.Y - Pan.Y) * ratio));
    }
}
