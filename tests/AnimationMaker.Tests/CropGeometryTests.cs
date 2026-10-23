using AnimationMaker.Core;
using AnimationMaker.Gpu;
using Xunit;

namespace AnimationMaker.Tests;

public sealed class CropGeometryTests
{
    [Theory]
    [InlineData(CropHandle.TopLeft, 110, 90, 190, 140)]
    [InlineData(CropHandle.Top, 100, 90, 200, 140)]
    [InlineData(CropHandle.TopRight, 100, 90, 210, 140)]
    [InlineData(CropHandle.Right, 100, 80, 210, 150)]
    [InlineData(CropHandle.BottomRight, 100, 80, 210, 160)]
    [InlineData(CropHandle.Bottom, 100, 80, 200, 160)]
    [InlineData(CropHandle.BottomLeft, 110, 80, 190, 160)]
    [InlineData(CropHandle.Left, 110, 80, 190, 150)]
    public void EachResizeHandleKeepsTheOppositeEdgeFixed(CropHandle handle, int x, int y, int w, int h)
    {
        Assert.Equal(new RectI(x, y, w, h), CropGeometry.Drag(new(100, 80, 200, 150), handle, new(0, 0), new(10, 10), new(1920, 1080)));
    }
    [Fact]
    public void MovingClampsWithoutChangingDimensionsAndResizingCannotInvert()
    {
        var crop = new RectI(100, 80, 200, 150); var bounds = new SizeI(1920, 1080);
        Assert.Equal(new RectI(1720, 930, 200, 150), CropGeometry.Drag(crop, CropHandle.Move, new(0, 0), new(99999, 99999), bounds));
        Assert.Equal(new RectI(0, 0, 200, 150), CropGeometry.Drag(crop, CropHandle.Move, new(0, 0), new(-99999, -99999), bounds));
        Assert.Equal(new RectI(299, 229, 1, 1), CropGeometry.Drag(crop, CropHandle.TopLeft, new(0, 0), new(99999, 99999), bounds));
        Assert.Equal(new RectI(100, 80, 1, 1), CropGeometry.Drag(crop, CropHandle.BottomRight, new(0, 0), new(-99999, -99999), bounds));
        foreach (var (handle, _) in CropGeometry.Handles(crop))
            foreach (var p in new[] { new PointD(-1e6, -1e6), new PointD(1e6, 1e6), new PointD(-1e6, 1e6), new PointD(1e6, -1e6) })
                FrameGeometry.ValidateCrop(CropGeometry.Drag(crop, handle, new(0, 0), p, bounds), bounds.Width, bounds.Height);
    }
    [Fact]
    public void DrawingWorksInReverseAndClampsAtEverySourceBoundary()
    {
        var bounds = new SizeI(1920, 1080);
        Assert.Equal(new RectI(100, 150, 400, 250), CropGeometry.Drag(new(0, 0, 1920, 1080), CropHandle.Draw, new(500, 400), new(100, 150), bounds));
        Assert.Equal(new RectI(0, 0, 1920, 1080), CropGeometry.Drag(new(0, 0, 1920, 1080), CropHandle.Draw, new(1920, 1080), new(-500, -500), bounds));
        Assert.Equal(new RectI(1919, 1079, 1, 1), CropGeometry.Drag(new(0, 0, 1920, 1080), CropHandle.Draw, new(1920, 1080), new(1920, 1080), bounds));
    }
    [Theory]
    [InlineData(800, 700, 1)]
    [InlineData(700, 900, 1)]
    [InlineData(800, 700, 3.2)]
    public void HandlesAndCoordinatesUseOriginal4KCanvasAtProxyResolutionAndZoom(double width, double height, double zoom)
    {
        var view = CropViewport.Fit(new(3840, 2160), new(910, 512), width, height, zoom);
        var crop = new RectI(400, 500, 2000, 1000);
        foreach (var (handle, point) in CropGeometry.Handles(crop))
        {
            var screen = view.Project(point); var restored = view.Unproject(screen);
            Assert.Equal(point.X, restored.X, 8); Assert.Equal(point.Y, restored.Y, 8);
            Assert.Equal(handle, CropGeometry.HitTest(crop, view, screen));
            Assert.Equal(handle, CropGeometry.HitTest(crop, view, new(screen.X + 8, screen.Y - 8)));
        }
        Assert.Equal(CropHandle.Move, CropGeometry.HitTest(crop, view, view.Project(new(1400, 1000))));
        Assert.Equal(CropHandle.None, CropGeometry.HitTest(crop, view, view.Project(new(50, 50))));
        Assert.False(view.Contains(new(view.Left - 1, view.Top)));
    }
    [Fact]
    public void VisualSelectionUsesTheSameSourcePixelsAsGpuCropAndProjectHistory()
    {
        var source = new RgbaFrame(32, 24, new byte[32 * 24 * 4]);
        for (int y = 0; y < 24; y++) for (int x = 0; x < 32; x++)
        {
            int i = (y * 32 + x) * 4;
            source.Pixels[i] = (byte)(x * 7); source.Pixels[i + 1] = (byte)(y * 9); source.Pixels[i + 2] = 70; source.Pixels[i + 3] = 255;
        }
        var view = CropViewport.Fit(new(32, 24), new(32, 24), 800, 700);
        var initial = new ProjectDocument(); var history = new EditHistory<ProjectDocument>(initial);
        var selected = CropGeometry.Drag(new(0, 0, 32, 24), CropHandle.Draw,
            view.Unproject(view.Project(new(5, 4))), view.Unproject(view.Project(new(25, 19))), new(32, 24));
        history.Set(initial with { Crop = selected });
        Assert.Equal(new RectI(5, 4, 20, 15), selected);
        using var gpu = new GpuFrameProcessor();
        var result = gpu.Process(source, new() { Enabled = false }, [], 0, selected, new(selected.Width, selected.Height));
        for (int y = 0; y < result.Height; y++)
            Assert.Equal(source.Pixels.AsSpan(((y + 4) * 32 + 5) * 4, 20 * 4).ToArray(), result.Pixels.AsSpan(y * 20 * 4, 20 * 4).ToArray());
        Assert.Null(history.Undo().Crop); Assert.Equal(selected, history.Redo().Crop);
    }
}
