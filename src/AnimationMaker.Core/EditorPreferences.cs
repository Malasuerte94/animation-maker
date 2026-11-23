using System.Text.Json;

namespace AnimationMaker.Core;

public enum ExportKind { Atlas, Delta, Webm, PackedMp4 }

public sealed record EditorPreferences
{
    public bool FullResolution { get; init; } = true;
    public ExportKind ExportFormat { get; init; } = ExportKind.Atlas;
    public int MaxFrameHeight { get; init; } = ExportSettings.DefaultMaxFrameHeight;
    public string[] ProtectedColors { get; init; } = [];
    public int ProtectTolerance { get; init; } = 12;

    public ProjectDocument NewClip(string path, VideoInfo info) => new()
    {
        SourcePath = path,
        TrimEnd = info.Duration,
        Fps = Math.Clamp(info.Fps, 1, 240),
        Keyer = new() { Enabled = !info.HasAlpha, ProtectedColors = ProtectedColors, ProtectTolerance = ProtectTolerance },
        Export = new() { MaxFrameHeight = MaxFrameHeight }
    };

    public static EditorPreferences Load(string path)
    {
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllText(path), ProjectStore.JsonOptions)
            ?? throw new InvalidDataException("Empty editor preferences.");
        if (value.MaxFrameHeight is < 1 or > 16384 || !Enum.IsDefined(value.ExportFormat))
            throw new InvalidDataException("Invalid export preferences.");
        new KeyerSettings { ProtectedColors = value.ProtectedColors, ProtectTolerance = value.ProtectTolerance }.ValidateProtectedColors();
        return value;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, ProjectStore.JsonOptions));
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
