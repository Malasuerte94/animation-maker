namespace AnimationMaker.Core;

public enum CropHandle { None, Move, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left, Draw }

// Coordinates are original source pixels; the viewport is in device-independent UI pixels.
public readonly record struct CropViewport(double Left, double Top, double Width, double Height, SizeI Source)
{
    public static CropViewport Fit(SizeI source, SizeI output, double width, double height, double zoom = 1)
    {
        double scale = Math.Min(width / output.Width, height / output.Height) * zoom;
        double w = output.Width * scale, h = output.Height * scale;
        return new((width - w) / 2, (height - h) / 2, w, h, source);
    }
    public PointD Project(PointD point) => new(Left + point.X / Source.Width * Width, Top + point.Y / Source.Height * Height);
    public PointD Unproject(PointD point) => new((point.X - Left) / Width * Source.Width, (point.Y - Top) / Height * Source.Height);
    public bool Contains(PointD point) => point.X >= Left && point.Y >= Top && point.X <= Left + Width && point.Y <= Top + Height;
}

public static class CropGeometry
{
    public static IEnumerable<(CropHandle Handle, PointD Point)> Handles(RectI crop)
    {
        double l = crop.X, t = crop.Y, r = l + crop.Width, b = t + crop.Height, x = (l + r) / 2, y = (t + b) / 2;
        yield return (CropHandle.TopLeft, new(l, t)); yield return (CropHandle.TopRight, new(r, t));
        yield return (CropHandle.BottomRight, new(r, b)); yield return (CropHandle.BottomLeft, new(l, b));
        yield return (CropHandle.Top, new(x, t)); yield return (CropHandle.Right, new(r, y));
        yield return (CropHandle.Bottom, new(x, b)); yield return (CropHandle.Left, new(l, y));
    }
    public static CropHandle HitTest(RectI crop, CropViewport view, PointD pointer, double radius = 9)
    {
        foreach (var (handle, position) in Handles(crop))
        {
            var p = view.Project(position);
            if (Math.Abs(pointer.X - p.X) <= radius && Math.Abs(pointer.Y - p.Y) <= radius) return handle;
        }
        var source = view.Unproject(pointer);
        return source.X >= crop.X && source.Y >= crop.Y && source.X <= crop.X + crop.Width && source.Y <= crop.Y + crop.Height
            ? CropHandle.Move : CropHandle.None;
    }
    public static RectI Drag(RectI start, CropHandle handle, PointD anchor, PointD pointer, SizeI bounds)
    {
        FrameGeometry.ValidateCrop(start, bounds.Width, bounds.Height);
        int dx = (int)Math.Round(Math.Clamp(pointer.X - anchor.X, -bounds.Width, bounds.Width));
        int dy = (int)Math.Round(Math.Clamp(pointer.Y - anchor.Y, -bounds.Height, bounds.Height));
        if (handle == CropHandle.Move)
            return start with { X = Math.Clamp(start.X + dx, 0, bounds.Width - start.Width), Y = Math.Clamp(start.Y + dy, 0, bounds.Height - start.Height) };
        if (handle == CropHandle.Draw)
        {
            int ax = Math.Clamp((int)Math.Round(anchor.X), 0, bounds.Width), ay = Math.Clamp((int)Math.Round(anchor.Y), 0, bounds.Height);
            int px = Math.Clamp((int)Math.Round(pointer.X), 0, bounds.Width), py = Math.Clamp((int)Math.Round(pointer.Y), 0, bounds.Height);
            int x = Math.Min(Math.Min(ax, px), bounds.Width - 1), y = Math.Min(Math.Min(ay, py), bounds.Height - 1);
            return new(x, y, Math.Max(1, Math.Max(ax, px) - x), Math.Max(1, Math.Max(ay, py) - y));
        }
        int l = start.X, t = start.Y, r = l + start.Width, b = t + start.Height;
        if (handle is CropHandle.TopLeft or CropHandle.Left or CropHandle.BottomLeft) l = Math.Clamp(l + dx, 0, r - 1);
        if (handle is CropHandle.TopRight or CropHandle.Right or CropHandle.BottomRight) r = Math.Clamp(r + dx, l + 1, bounds.Width);
        if (handle is CropHandle.TopLeft or CropHandle.Top or CropHandle.TopRight) t = Math.Clamp(t + dy, 0, b - 1);
        if (handle is CropHandle.BottomLeft or CropHandle.Bottom or CropHandle.BottomRight) b = Math.Clamp(b + dy, t + 1, bounds.Height);
        return new(l, t, r - l, b - t);
    }
}
