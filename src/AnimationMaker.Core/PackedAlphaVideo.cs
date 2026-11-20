using System.Globalization;

namespace AnimationMaker.Core;

public static class PackedAlphaVideo
{
    private const string CommentPrefix = "AnimationMaker RGBAlpha v1 ";

    public static SizeI EncodedSize(SizeI size) =>
        new(checked(2 * (size.Width + size.Width % 2)), checked(size.Height + size.Height % 2));

    internal static string Comment(SizeI size) =>
        FormattableString.Invariant($"{CommentPrefix}{size.Width}x{size.Height}");

    internal static SizeI? ReadComment(string? comment, SizeI encodedSize)
    {
        if (comment == null || !comment.StartsWith(CommentPrefix, StringComparison.Ordinal)) return null;
        string[] dimensions = comment[CommentPrefix.Length..].Split('x');
        if (dimensions.Length != 2 ||
            !int.TryParse(dimensions[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width) ||
            !int.TryParse(dimensions[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height) ||
            width <= 0 || width > encodedSize.Width / 2 || height <= 0 || height > encodedSize.Height)
            return null;
        var size = new SizeI(width, height);
        return EncodedSize(size) == encodedSize ? size : null;
    }

    public static void Pack(RgbaFrame frame, Span<byte> rgb)
    {
        frame.Validate();
        var encoded = EncodedSize(new(frame.Width, frame.Height));
        if (rgb.Length != checked(encoded.Width * encoded.Height * 3))
            throw new ArgumentException("The RGB buffer must match the packed video dimensions.", nameof(rgb));

        // Padding is black in the color half and transparent in the mask half.
        rgb.Clear();
        int maskOffset = encoded.Width / 2 * 3;
        for (int y = 0; y < frame.Height; y++)
        {
            int input = y * frame.Width * 4;
            int color = y * encoded.Width * 3;
            int mask = color + maskOffset;
            for (int x = 0; x < frame.Width; x++, input += 4, color += 3, mask += 3)
            {
                rgb[color] = frame.Pixels[input];
                rgb[color + 1] = frame.Pixels[input + 1];
                rgb[color + 2] = frame.Pixels[input + 2];
                rgb[mask] = rgb[mask + 1] = rgb[mask + 2] = frame.Pixels[input + 3];
            }
        }
    }

    public static RgbaFrame Unpack(RgbaFrame frame, SizeI size)
    {
        frame.Validate();
        int halfWidth = frame.Width / 2;
        if (frame.Width % 2 != 0 || size.Width <= 0 || size.Width > halfWidth || size.Height <= 0 || size.Height > frame.Height)
            throw new InvalidDataException("The video does not have matching color and alpha halves.");

        var pixels = new byte[checked(size.Width * size.Height * 4)];
        int rowBytes = size.Width * 4;
        for (int y = 0; y < size.Height; y++)
        {
            int color = y * frame.Width * 4;
            int mask = color + halfWidth * 4;
            int output = y * rowBytes;
            frame.Pixels.AsSpan(color, rowBytes).CopyTo(pixels.AsSpan(output, rowBytes));
            for (int x = 0; x < size.Width; x++)
                pixels[output + x * 4 + 3] = frame.Pixels[mask + x * 4];
        }
        return new(size.Width, size.Height, pixels);
    }
}
