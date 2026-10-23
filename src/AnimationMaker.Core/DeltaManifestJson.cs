using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AnimationMaker.Core;

/// <summary>Compact v2 on disk; keep the descriptive model for playback and legacy v1 imports.</summary>
public static class DeltaManifestJson
{
    public static string Serialize(DeltaManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("format", manifest.Format); writer.WriteNumber("version", 2);
            writer.WriteNumber("width", manifest.Width); writer.WriteNumber("height", manifest.Height); writer.WriteNumber("fps", manifest.Fps);
            writer.WritePropertyName("pages"); writer.WriteStartArray();
            foreach (var page in manifest.Pages) { writer.WriteStartArray(); writer.WriteStringValue(page.Image); writer.WriteNumberValue(page.Width); writer.WriteNumberValue(page.Height); writer.WriteEndArray(); }
            writer.WriteEndArray();
            var uses = manifest.Frames.SelectMany(f => f.Operations).Where(o => o.Patch != null).GroupBy(o => o.Patch!).ToDictionary(g => g.Key, g => g.Count());
            var ordered = manifest.Patches.OrderByDescending(p => uses.GetValueOrDefault(p.Key)).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
            var ids = ordered.Select((p, i) => (p.Key, i)).ToDictionary(p => p.Key, p => p.i);
            writer.WritePropertyName("patches"); writer.WriteStartArray();
            foreach (var pair in ordered)
            {
                var patch = pair.Value;
                writer.WriteStartArray();
                foreach (int n in new[] { patch.Page, patch.Rect.X, patch.Rect.Y, patch.Rect.Width, patch.Rect.Height }) writer.WriteNumberValue(n);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("keyframes"); JsonSerializer.Serialize(writer, manifest.Keyframes);
            writer.WritePropertyName("frames"); writer.WriteStartArray();
            foreach (var frame in manifest.Frames)
            {
                writer.WriteStartArray();
                foreach (var op in frame.Operations)
                {
                    writer.WriteNumberValue(op.Kind == "clear" ? -1 : ids[op.Patch!]);
                    writer.WriteNumberValue(op.X); writer.WriteNumberValue(op.Y);
                    if (op.Kind == "clear") { writer.WriteNumberValue(op.Width); writer.WriteNumberValue(op.Height); }
                }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            if (manifest.Encoding != null) { writer.WritePropertyName("encoding"); JsonSerializer.Serialize(writer, manifest.Encoding, ProjectStore.JsonOptions); }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static DeltaManifest Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("version").GetInt32() != 2 ||
            (root.TryGetProperty("frames", out var expanded) && expanded.ValueKind == JsonValueKind.Array && expanded.GetArrayLength() > 0 && expanded[0].ValueKind == JsonValueKind.Object))
            return JsonSerializer.Deserialize<DeltaManifest>(json, ProjectStore.JsonOptions) ?? throw new InvalidDataException("Empty delta manifest.");
        static int[] Tuple(JsonElement element, int length)
        {
            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != length) throw new InvalidDataException("Invalid compact delta tuple.");
            return element.EnumerateArray().Select(n => n.GetInt32()).ToArray();
        }
        var pages = new List<DeltaPage>();
        foreach (var p in root.GetProperty("pages").EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 3) throw new InvalidDataException("Invalid compact delta page.");
            pages.Add(new(p[0].GetString()!, p[1].GetInt32(), p[2].GetInt32()));
        }
        var patches = new Dictionary<string, DeltaPatch>();
        foreach (var p in root.GetProperty("patches").EnumerateArray())
        {
            int[] t = Tuple(p, 5);
            patches.Add(patches.Count.ToString(CultureInfo.InvariantCulture), new(t[0], new(t[1], t[2], t[3], t[4])));
        }
        var keys = root.GetProperty("keyframes").EnumerateArray().Select(k => k.GetInt32()).ToList();
        var keySet = keys.ToHashSet(); double fps = root.GetProperty("fps").GetDouble();
        var frames = new List<DeltaFrame>();
        foreach (var frame in root.GetProperty("frames").EnumerateArray())
        {
            if (frame.ValueKind != JsonValueKind.Array || frame.GetArrayLength() > DeltaAtlasExporter.MaximumOperations * 5) throw new InvalidDataException("Invalid compact delta frame.");
            int[] values = frame.EnumerateArray().Select(n => n.GetInt32()).ToArray();
            var ops = new List<DeltaOperation>();
            for (int i = 0; i < values.Length;)
            {
                int id = values[i++];
                int count = id == -1 ? 4 : 2;
                if (id < -1 || i + count > values.Length || ops.Count >= DeltaAtlasExporter.MaximumOperations) throw new InvalidDataException("Invalid compact delta operation.");
                int x = values[i++], y = values[i++];
                if (id == -1) { ops.Add(new("clear", x, y, values[i++], values[i++])); continue; }
                string name = id.ToString(CultureInfo.InvariantCulture);
                if (!patches.TryGetValue(name, out var patch)) throw new InvalidDataException("Invalid compact patch reference.");
                ops.Add(new("replace", x, y, patch.Rect.Width, patch.Rect.Height, name));
            }
            frames.Add(new(1000 / fps, keySet.Contains(frames.Count), ops, ""));
        }
        var encoding = root.TryGetProperty("encoding", out var e) ? e.Deserialize<DeltaEncoding>(ProjectStore.JsonOptions) : null;
        return new(root.GetProperty("format").GetString()!, 2, root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32(), fps, pages, patches, keys, frames, encoding);
    }
}
