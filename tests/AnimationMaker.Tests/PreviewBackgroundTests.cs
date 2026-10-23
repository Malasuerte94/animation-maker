using AnimationMaker.Core;
using AnimationMaker.Gpu;
using Xunit;

namespace AnimationMaker.Tests;

public sealed class PreviewBackgroundTests
{
    private static RgbaFrame Solid(int width, int height, byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = a; }
        return new(width, height, pixels);
    }
    private static byte[] Pixel(RgbaFrame image, int x, int y) => image.Pixels[((y * image.Width + x) * 4)..((y * image.Width + x) * 4 + 4)];

    [Fact]
    public void PhotoCompositionUsesAlphaAndDoesNotChangeTransparentExport()
    {
        using var gpu = new GpuFrameProcessor();
        var subject = Solid(4, 4, 255, 0, 0, 128);
        var original = gpu.Process(subject, new() { Enabled = false }, [], 0, new(0, 0, 4, 4), new(4, 4));
        gpu.SetPreviewBackgroundImage(Solid(8, 8, 0, 0, 255, 255));
        var composite = gpu.ReadPreview(new(4, 4), PreviewBackground.Image);
        var center = Pixel(composite, 2, 2);
        Assert.InRange((int)center[0], 186, 190);
        Assert.Equal(0, center[1]); Assert.InRange((int)center[2], 185, 190); Assert.Equal(255, center[3]);
        Assert.Equal(original.Pixels, gpu.Readback().Pixels);
        var nextFrame = gpu.Process(subject, new() { Enabled = false }, [], 1, new(0, 0, 4, 4), new(4, 4));
        Assert.Equal(original.Pixels, nextFrame.Pixels);
        Assert.Equal(composite.Pixels, gpu.ReadPreview(new(4, 4), PreviewBackground.Image).Pixels);
    }
    [Fact]
    public void FillCropsAndFitPreservesTheWholePhotoWithoutStretching()
    {
        using var gpu = new GpuFrameProcessor();
        var subject = Solid(6, 6, 0, 0, 0, 0);
        gpu.Process(subject, new() { Enabled = false }, [], 0, new(0, 0, 6, 6), new(6, 6));
        var photo = Solid(6, 2, 0, 255, 0, 255);
        for (int y = 0; y < 2; y++) for (int x = 0; x < 6; x++)
        {
            int i = (y * 6 + x) * 4;
            if (x < 2) { photo.Pixels[i] = 255; photo.Pixels[i + 1] = 0; }
            if (x > 3) { photo.Pixels[i + 1] = 0; photo.Pixels[i + 2] = 255; }
        }
        gpu.SetPreviewBackgroundImage(photo);
        var fill = gpu.ReadPreview(new(6, 6), PreviewBackground.Image, imageFit: BackgroundImageFit.Fill);
        var fit = gpu.ReadPreview(new(6, 6), PreviewBackground.Image, imageFit: BackgroundImageFit.Fit);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(fill, 2, 2));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(fit, 0, 2));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(fit, 5, 2));
        var letterbox = Pixel(fit, 0, 0);
        Assert.InRange((int)letterbox[0], 35, 65); Assert.InRange((int)letterbox[1], 40, 70); Assert.Equal(255, letterbox[3]);
    }
    [Fact]
    public void TransparentPhotoAndImageRemovalFallBackToCheckerboard()
    {
        using var gpu = new GpuFrameProcessor();
        var frame = Solid(4, 4, 0, 0, 0, 0);
        gpu.Process(frame, new() { Enabled = false }, [], 0, new(0, 0, 4, 4), new(4, 4));
        var checker = gpu.ReadPreview(new(4, 4), PreviewBackground.Checkerboard);
        gpu.SetPreviewBackgroundImage(Solid(4, 4, 255, 0, 0, 0));
        Assert.Equal(checker.Pixels, gpu.ReadPreview(new(4, 4), PreviewBackground.Image).Pixels);
        gpu.SetPreviewBackgroundImage(null);
        Assert.Equal(checker.Pixels, gpu.ReadPreview(new(4, 4), PreviewBackground.Image).Pixels);
    }
    [Fact]
    public void AlphaMatteModeStillShowsAlphaWhenAPhotoIsLoaded()
    {
        using var gpu = new GpuFrameProcessor();
        var frame = Solid(4, 4, 255, 0, 0, 128);
        gpu.Process(frame, new() { Enabled = false }, [], 0, new(0, 0, 4, 4), new(4, 4));
        gpu.SetPreviewBackgroundImage(Solid(4, 4, 0, 255, 0, 255));
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, Pixel(gpu.ReadPreview(new(4, 4), PreviewBackground.Alpha), 2, 2));
    }
}
