using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimationMaker.Core;

public enum KeyerMode { Quality, Fast }
public enum MaskKind { Keep, Remove }
public enum AtlasLayout { Packed, Grid }
public enum AtlasImageFormat { Webp, Png }
public enum VideoQuality { High, Balanced, Small, Lossless }
public enum DeltaAnalysisBackend { Auto, Cpu, Gpu }
public enum PreviewBackground { Checkerboard, White, Black, Magenta, Alpha, Image }
public enum BackgroundImageFit { Fill, Fit }
public readonly record struct PointD(double X, double Y);
public readonly record struct RectI(int X, int Y, int Width, int Height);
public readonly record struct SizeI(int Width, int Height);

public sealed record KeyerSettings
{
    public bool Enabled { get; init; } = true;
    public KeyerMode Mode { get; init; } = KeyerMode.Quality;
    public string ScreenColor { get; init; } = "#00FF00";
    public string? CleanPlatePath { get; init; }
    public float Similarity { get; init; } = .10f;
    public float Softness { get; init; } = .12f;
    public float ClipBlack { get; init; } = .015f;
    public float ClipWhite { get; init; } = .98f;
    public float Despill { get; init; } = 1f;
    public float RedWeight { get; init; } = .5f;
    public float BlueWeight { get; init; } = .5f;
    public float Choke { get; init; }
    public float Feather { get; init; }
}

public sealed record MaskKeyframe(double Time, List<PointD> Points);
public sealed record PolygonMask
{
    public string Name { get; init; } = "Mask";
    public MaskKind Kind { get; init; }
    public bool Enabled { get; init; } = true;
    public List<MaskKeyframe> Keyframes { get; init; } = [];

    public IReadOnlyList<PointD> At(double time)
    {
        if (Keyframes.Count == 0) return [];
        var sorted = Keyframes.OrderBy(k => k.Time).ToArray();
        if (time <= sorted[0].Time) return sorted[0].Points;
        for (int i = 1; i < sorted.Length; i++)
        {
            if (time > sorted[i].Time) continue;
            var a = sorted[i - 1]; var b = sorted[i];
            if (a.Points.Count != b.Points.Count || b.Time <= a.Time) return a.Points;
            double t = (time - a.Time) / (b.Time - a.Time);
            return a.Points.Zip(b.Points, (p, q) => new PointD(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t)).ToArray();
        }
        return sorted[^1].Points;
    }
}

public sealed record ExportSettings
{
    public int MaxFrameHeight { get; init; } = 512;
    public int MaxAtlasSize { get; init; } = 4096;
    public AtlasLayout Layout { get; init; }
    public int Padding { get; init; } = 2;
    public int Extrusion { get; init; } = 1;
    public bool Deduplicate { get; init; } = true;
    public AtlasImageFormat ImageFormat { get; init; } = AtlasImageFormat.Webp;
    public int WebpQuality { get; init; } = 75;
    public VideoQuality VideoQuality { get; init; } = VideoQuality.High;
    public bool IncludeAudio { get; init; }
    public int DeltaTolerance { get; init; } = 8;
    public DeltaAnalysisBackend DeltaAnalysisBackend { get; init; } = DeltaAnalysisBackend.Auto;

    [JsonIgnore]
    public string ImageExtension => ImageFormat == AtlasImageFormat.Webp ? ".webp" : ".png";
    public void ValidateImageEncoding()
    {
        if (!Enum.IsDefined(ImageFormat)) throw new InvalidDataException("Unsupported atlas image format.");
        if (!Enum.IsDefined(DeltaAnalysisBackend)) throw new InvalidDataException("Unsupported Delta analysis backend.");
        if (WebpQuality is < 1 or > 100) throw new InvalidDataException("WebP quality must be between 1 and 100.");
    }
}

public sealed record ProjectDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string SourcePath { get; init; } = "";
    public double TrimStart { get; init; }
    public double TrimEnd { get; init; }
    public double Fps { get; init; } = 30;
    public RectI? Crop { get; init; }
    public KeyerSettings Keyer { get; init; } = new();
    public List<PolygonMask> Masks { get; init; } = [];
    public ExportSettings Export { get; init; } = new();

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException($"Unsupported project version: {SchemaVersion}.");
        if (!File.Exists(SourcePath)) throw new FileNotFoundException("Source video was not found.", SourcePath);
        if (!double.IsFinite(Fps) || Fps < 1 || Fps > 240) throw new InvalidDataException("FPS must be between 1 and 240.");
        if (!double.IsFinite(TrimStart) || !double.IsFinite(TrimEnd) || TrimStart < 0 || TrimEnd <= TrimStart)
            throw new InvalidDataException("Choose a valid in/out range.");
        if (Export.MaxFrameHeight is < 1 or > 16384 || Export.MaxAtlasSize is not (2048 or 4096 or 8192))
            throw new InvalidDataException("Invalid export dimensions.");
        if (Export.DeltaTolerance is < 0 or > 32) throw new InvalidDataException("Delta tolerance must be between 0 and 32.");
        Export.ValidateImageEncoding();
        if (Keyer.ClipWhite <= Keyer.ClipBlack) throw new InvalidDataException("White clip must exceed black clip.");
        float[] controls = [Keyer.Similarity, Keyer.Softness, Keyer.ClipBlack, Keyer.ClipWhite, Keyer.Despill, Keyer.RedWeight, Keyer.BlueWeight, Keyer.Choke, Keyer.Feather];
        if (controls.Any(v => !float.IsFinite(v)) || Keyer.Similarity is < 0 or > 1 || Keyer.Softness is <= 0 or > 1 || Keyer.Despill is < 0 or > 1 || Keyer.ClipBlack is < 0 or > 1 || Keyer.ClipWhite is < 0 or > 1 || Keyer.RedWeight is < 0 or > 1 || Keyer.BlueWeight is < 0 or > 1 || Keyer.Choke is < -4 or > 4 || Keyer.Feather is < 0 or > 2.5f)
            throw new InvalidDataException("Keying controls contain invalid values.");
        if (!Enum.IsDefined(Keyer.Mode) || !Enum.IsDefined(Export.Layout) || !Enum.IsDefined(Export.VideoQuality) || Export.Padding is < 0 or > 32 || Export.Extrusion is < 0 or > 8)
            throw new InvalidDataException("Unsupported export or keyer settings.");
        _ = ColorMath.ParseHex(Keyer.ScreenColor);
        foreach (var mask in Masks)
            foreach (var key in mask.Keyframes)
                if (!double.IsFinite(key.Time) || key.Time < 0 || key.Points.Count < 3 || key.Points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1))
                    throw new InvalidDataException("Mask vertices must be normalized coordinates in [0, 1].");
        if (Keyer.CleanPlatePath is { Length: > 0 } plate && !File.Exists(plate)) throw new FileNotFoundException("Clean plate was not found.", plate);
    }

    public SizeI OutputSize(VideoInfo info)
    {
        var size = Crop is { } c ? new SizeI(c.Width, c.Height) : new SizeI(info.Width, info.Height);
        return FrameGeometry.FitHeight(size, Export.MaxFrameHeight);
    }
}

public sealed record VideoInfo(int Width, int Height, double Duration, double Fps, bool HasAudio, bool HasAlpha, string CodecName = "");
public sealed record RgbaFrame(int Width, int Height, byte[] Pixels)
{
    public long ByteLength => Pixels.LongLength;
    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || Pixels.Length != checked(Width * Height * 4)) throw new InvalidDataException("Invalid RGBA frame.");
    }
}
public sealed record ExportProgress(string Stage, int Completed, int Total)
{
    public double Fraction => Total <= 0 ? 0 : (double)Completed / Total;
}

public static class FrameGeometry
{
    public static SizeI FitHeight(SizeI size, int maxHeight)
    {
        if (size.Width <= 0 || size.Height <= 0 || maxHeight <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (size.Height <= maxHeight) return size;
        return new(Math.Max(1, (int)Math.Round((double)size.Width * maxHeight / size.Height)), maxHeight);
    }

    public static RectI ValidateCrop(RectI? requested, int width, int height)
    {
        if (requested is not { } crop) return new(0, 0, width, height);
        if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 || (long)crop.X + crop.Width > width || (long)crop.Y + crop.Height > height)
            throw new InvalidDataException("Crop must be inside the source frame.");
        return crop;
    }

    public static int FrameCount(double start, double end, double fps) => checked((int)Math.Ceiling((end - start) * fps - 1e-7));
}

public static class ProjectStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
    public static async Task SaveAsync(string path, ProjectDocument project, CancellationToken ct = default)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var stored = project with { SourcePath = Path.GetRelativePath(folder, project.SourcePath), Keyer = project.Keyer with { CleanPlatePath = project.Keyer.CleanPlatePath is { Length: > 0 } plate ? Path.GetRelativePath(folder, plate) : null } };
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(stored, JsonOptions), ct);
        File.Move(temporary, path, true);
    }
    public static async Task<ProjectDocument> LoadAsync(string path, CancellationToken ct = default)
    {
        var project = JsonSerializer.Deserialize<ProjectDocument>(await File.ReadAllTextAsync(path, ct), JsonOptions) ?? throw new InvalidDataException("Empty project.");
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        project = project with { SourcePath = Path.GetFullPath(project.SourcePath, folder), Keyer = project.Keyer with { CleanPlatePath = project.Keyer.CleanPlatePath is { Length: > 0 } plate ? Path.GetFullPath(plate, folder) : null } };
        project.Validate();
        return project;
    }
}

public sealed class EditHistory<T>(T initial) where T : class
{
    private readonly List<T> _undo = [];
    private readonly Stack<T> _redo = new();
    public T Current { get; private set; } = initial;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public void Set(T value)
    {
        if (EqualityComparer<T>.Default.Equals(Current, value)) return;
        _undo.Add(Current);
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear(); Current = value;
    }
    public T Undo() { if (_undo.Count == 0) return Current; _redo.Push(Current); Current = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); return Current; }
    public T Redo() { if (_redo.Count == 0) return Current; _undo.Add(Current); Current = _redo.Pop(); return Current; }
}

public static class ColorMath
{
    public static (float R, float G, float B) ParseHex(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#' || !int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var value))
            throw new InvalidDataException("Use a screen color such as #00FF00.");
        return (((value >> 16) & 255) / 255f, ((value >> 8) & 255) / 255f, (value & 255) / 255f);
    }
}
