using SkiaSharp;

namespace AnimationMaker.Core;

public static class Imaging
{
    public static RgbaFrame Load(string path, bool applyOrientation = false)
    {
        // Decode from owned memory so the native codec cannot retain a Windows file handle.
        using var data = SKData.CreateCopy(File.ReadAllBytes(path));
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException($"Cannot decode image: {path}");
        using var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) throw new InvalidDataException("Image decoding failed.");
        var frame = new RgbaFrame(bitmap.Width, bitmap.Height, bitmap.Bytes);
        return applyOrientation ? Orient(frame, codec.EncodedOrigin) : frame;
    }
    private static RgbaFrame Orient(RgbaFrame image, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return image;
        bool transpose = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int width = transpose ? image.Height : image.Width, height = transpose ? image.Width : image.Height;
        var pixels = new byte[image.Pixels.Length];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            (int sx, int sy) = origin switch
            {
                SKEncodedOrigin.TopRight => (image.Width - 1 - x, y),
                SKEncodedOrigin.BottomRight => (image.Width - 1 - x, image.Height - 1 - y),
                SKEncodedOrigin.BottomLeft => (x, image.Height - 1 - y),
                SKEncodedOrigin.LeftTop => (y, x),
                SKEncodedOrigin.RightTop => (y, image.Height - 1 - x),
                SKEncodedOrigin.RightBottom => (image.Width - 1 - y, image.Height - 1 - x),
                SKEncodedOrigin.LeftBottom => (image.Width - 1 - y, x),
                _ => (x, y)
            };
            Buffer.BlockCopy(image.Pixels, (sy * image.Width + sx) * 4, pixels, (y * width + x) * 4, 4);
        }
        return new(width, height, pixels);
    }
    public static void SavePng(string path, RgbaFrame frame)
        => File.WriteAllBytes(path, EncodePng(frame));

    public static void SaveAtlasImage(string path, RgbaFrame frame, ExportSettings settings)
    {
        settings.ValidateImageEncoding();
        File.WriteAllBytes(path, settings.ImageFormat == AtlasImageFormat.Webp ? EncodeWebp(frame, settings.WebpQuality) : EncodePng(frame));
    }

    public static byte[] EncodeWebp(RgbaFrame frame, int quality)
    {
        frame.Validate();
        if (quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quality));
        using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, 0, bitmap.GetPixels(), frame.Pixels.Length);
        using var pixmap = bitmap.PeekPixels();
        using var encoded = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, quality))
            ?? throw new InvalidDataException("WebP encoding failed.");
        return encoded.ToArray();
    }

    public static byte[] EncodePng(RgbaFrame frame)
    {
        frame.Validate();
        using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, 0, bitmap.GetPixels(), frame.Pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
    public static RgbaFrame Resize(RgbaFrame frame, int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, 0, bitmap.GetPixels(), frame.Pixels.Length);
        using var resized = bitmap.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) ?? throw new InvalidDataException("Resize failed.");
        return new(width, height, resized.Bytes);
    }
    public static string SampleScreen(RgbaFrame frame)
    {
        // Border samples are only accepted when green dominates, avoiding foreground colors.
        var samples = new List<(byte R, byte G, byte B)>();
        int border = Math.Max(1, Math.Min(frame.Width, frame.Height) / 12);
        for (int y = 0; y < frame.Height; y += Math.Max(1, frame.Height / 100))
            for (int x = 0; x < frame.Width; x += Math.Max(1, frame.Width / 100))
            {
                if (x >= border && y >= border && x < frame.Width - border && y < frame.Height - border) continue;
                int i = (y * frame.Width + x) * 4;
                if (frame.Pixels[i + 1] > frame.Pixels[i] * 1.25 + 8 && frame.Pixels[i + 1] > frame.Pixels[i + 2] * 1.25 + 8)
                    samples.Add((frame.Pixels[i], frame.Pixels[i + 1], frame.Pixels[i + 2]));
            }
        if (samples.Count < 5) throw new InvalidDataException("No reliable green screen sample. Use the eyedropper to pick the background.");
        byte median(IEnumerable<byte> values) => values.Order().ElementAt(samples.Count / 2);
        return $"#{median(samples.Select(p => p.R)):X2}{median(samples.Select(p => p.G)):X2}{median(samples.Select(p => p.B)):X2}";
    }
    public static byte[] RasterizeMasks(int width, int height, IEnumerable<PolygonMask> masks, double time)
    {
        var result = new byte[checked(width * height * 4)];
        foreach (var mask in masks.Where(m => m.Enabled))
        {
            var points = mask.At(time);
            if (points.Count < 3) continue;
            // Scanline fill is deterministic and preserves separate keep/remove channels.
            int channel = mask.Kind == MaskKind.Keep ? 0 : 1;
            var intersections = new List<double>(points.Count);
            for (int y = 0; y < height; y++)
            {
                double py = (y + .5) / height;
                intersections.Clear();
                for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
                {
                    var a = points[j]; var b = points[i];
                    if ((a.Y <= py && b.Y > py) || (b.Y <= py && a.Y > py)) intersections.Add((a.X + (py - a.Y) / (b.Y - a.Y) * (b.X - a.X)) * width);
                }
                intersections.Sort();
                for (int i = 0; i + 1 < intersections.Count; i += 2)
                    for (int x = Math.Max(0, (int)Math.Ceiling(intersections[i] - .5)); x < Math.Min(width, (int)Math.Ceiling(intersections[i + 1] - .5)); x++)
                        result[(y * width + x) * 4 + channel] = 255;
            }
        }
        return result;
    }
}

public sealed class FrameCache(long capacityBytes = 256L * 1024 * 1024)
{
    private readonly Dictionary<string, LinkedListNode<(string Key, RgbaFrame Frame)>> _items = new();
    private readonly LinkedList<(string Key, RgbaFrame Frame)> _lru = new();
    public long UsedBytes { get; private set; }
    public RgbaFrame? Get(string key)
    {
        lock (_items) { if (!_items.TryGetValue(key, out var node)) return null; _lru.Remove(node); _lru.AddFirst(node); return node.Value.Frame; }
    }
    public void Put(string key, RgbaFrame frame)
    {
        lock (_items)
        {
            if (_items.Remove(key, out var old)) { UsedBytes -= old.Value.Frame.ByteLength; _lru.Remove(old); }
            if (frame.ByteLength > capacityBytes) return;
            while (_lru.Last is { } last && UsedBytes + frame.ByteLength > capacityBytes) { UsedBytes -= last.Value.Frame.ByteLength; _items.Remove(last.Value.Key); _lru.RemoveLast(); }
            _items[key] = _lru.AddFirst((key, frame)); UsedBytes += frame.ByteLength;
        }
    }
    public void Clear() { lock (_items) { _items.Clear(); _lru.Clear(); UsedBytes = 0; } }
}
