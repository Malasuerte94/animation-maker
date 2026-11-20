using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AnimationMaker.Core;

public sealed class Ffmpeg
{
    public string FfmpegPath { get; }
    public string FfprobePath { get; }
    public Ffmpeg(string? directory = null)
    {
        directory ??= LocateDirectory();
        FfmpegPath = Path.Combine(directory, "ffmpeg.exe"); FfprobePath = Path.Combine(directory, "ffprobe.exe");
        if (!File.Exists(FfmpegPath) || !File.Exists(FfprobePath)) throw new FileNotFoundException("FFmpeg is missing. Run tools/bootstrap.ps1, then rebuild.");
    }
    private static string LocateDirectory()
    {
        if (Environment.GetEnvironmentVariable("ANIMATION_MAKER_FFMPEG") is { Length: > 0 } configured) return configured;
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
            foreach (var relative in new[] { "ffmpeg", Path.Combine(".tools", "ffmpeg") })
                if (File.Exists(Path.Combine(folder.FullName, relative, "ffmpeg.exe"))) return Path.Combine(folder.FullName, relative);
        return Path.Combine(AppContext.BaseDirectory, "ffmpeg");
    }
    public static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    public static Process Start(string executable, IEnumerable<string> args, bool input = false)
    {
        var psi = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = input };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return Process.Start(psi) ?? throw new IOException($"Could not start {Path.GetFileName(executable)}.");
    }
    public async Task<VideoInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        using var process = Start(FfprobePath, ["-v", "error", "-show_streams", "-show_format", "-of", "json", path]);
        using var registration = ct.Register(() => Kill(process));
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidDataException(await errors);
        using var json = JsonDocument.Parse(await output);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.TryGetProperty("codec_type", out var type) && type.GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("The file contains no video stream.");
        if (video.TryGetProperty("color_transfer", out var transfer) && transfer.GetString() is "smpte2084" or "arib-std-b67") throw new InvalidDataException("This version processes SDR video. Convert HDR footage to SDR before importing it.");
        int width = video.GetProperty("width").GetInt32(), height = video.GetProperty("height").GetInt32();
        if (video.TryGetProperty("side_data_list", out var side))
            foreach (var item in side.EnumerateArray())
                if (item.TryGetProperty("rotation", out var rotation) && Math.Abs(rotation.GetDouble()) % 180 == 90) (width, height) = (height, width);
        double duration = 0;
        SizeI? packedAlpha = null;
        if (json.RootElement.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("tags", out var formatTags) && formatTags.TryGetProperty("comment", out var comment))
                packedAlpha = PackedAlphaVideo.ReadComment(comment.GetString(), new(width, height));
            if (format.TryGetProperty("duration", out var d)) double.TryParse(d.GetString(), CultureInfo.InvariantCulture, out duration);
        }
        // An AAC track may make the container slightly longer than the packed video.
        if ((duration <= 0 || packedAlpha.HasValue) && video.TryGetProperty("duration", out var videoDuration) &&
            double.TryParse(videoDuration.GetString(), CultureInfo.InvariantCulture, out double seconds) && seconds > 0)
            duration = seconds;
        double fps = 30;
        if (video.TryGetProperty("avg_frame_rate", out var rate))
        {
            var parts = rate.GetString()?.Split('/') ?? [];
            if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n) && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var den) && den > 0 && n > 0) fps = n / den;
        }
        if (packedAlpha.HasValue && video.TryGetProperty("nb_frames", out var frameCount) &&
            int.TryParse(frameCount.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out int frames) && frames > 0)
            duration = frames / fps;
        string pixelFormat = video.TryGetProperty("pix_fmt", out var pix) ? pix.GetString() ?? "" : "";
        bool alpha = pixelFormat.StartsWith("yuva", StringComparison.Ordinal) || pixelFormat.StartsWith("gbrap", StringComparison.Ordinal) || pixelFormat is "rgba" or "bgra" or "argb" or "abgr" || pixelFormat.StartsWith("ya", StringComparison.Ordinal);
        if (video.TryGetProperty("tags", out var tags)) alpha |= tags.EnumerateObject().Any(p => p.Name.Equals("alpha_mode", StringComparison.OrdinalIgnoreCase) && p.Value.ToString() == "1");
        if (width <= 0 || height <= 0 || !double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("Invalid video dimensions or duration.");
        return new(width, height, duration, fps, streams.Any(s => s.TryGetProperty("codec_type", out var type) && type.GetString() == "audio"), alpha, video.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "" : "")
        { PackedAlphaSize = packedAlpha };
    }

    public async IAsyncEnumerable<RgbaFrame> DecodeAsync(string path, VideoInfo info, double start, double end, double fps, int maxHeight = 16384, bool hardware = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string timing = $"fps={Number(fps)}:start_time=0:eof_action=pass,tpad=stop_mode=clone:stop_duration=1,";
        int count = FrameGeometry.FrameCount(start, end, fps);
        await foreach (var frame in DecodeFramesAsync(path, info, start, maxHeight, hardware, timing, count, ct))
            yield return frame;
    }

    internal IAsyncEnumerable<RgbaFrame> DecodeEncodedFramesAsync(string path, VideoInfo info, CancellationToken ct) =>
        DecodeFramesAsync(path, info, 0, info.Height, false, "", null, ct);

    private async IAsyncEnumerable<RgbaFrame> DecodeFramesAsync(string path, VideoInfo info, double start, int maxHeight, bool hardware,
        string timing, int? count, [EnumeratorCancellation] CancellationToken ct)
    {
        var size = FrameGeometry.FitHeight(new(info.Width, info.Height), maxHeight);
        var args = new List<string> { "-v", "error", "-nostdin", "-threads", "2" };
        // libvpx explicitly decodes the auxiliary alpha stream; native VP9 decoding may omit it.
        if (info.HasAlpha && info.CodecName is "vp9" or "vp8") args.AddRange(["-c:v", info.CodecName == "vp8" ? "libvpx" : "libvpx-vp9"]);
        else if (hardware) args.AddRange(["-hwaccel", "d3d11va"]);
        // Bound by frame count rather than -t: FFmpeg may discard the last partial-duration frame with -t.
        args.AddRange(["-ss", Number(start), "-i", path, "-map", "0:v:0", "-an", "-sn", "-dn", "-filter_threads", "2",
            "-vf", $"{timing}scale={size.Width}:{size.Height}:flags=lanczos,format=rgba"]);
        if (count is { } frames) args.AddRange(["-frames:v", frames.ToString(CultureInfo.InvariantCulture)]);
        else args.AddRange(["-fps_mode", "passthrough"]);
        args.AddRange(["-f", "rawvideo", "pipe:1"]);
        using var process = Start(FfmpegPath, args);
        using var registration = ct.Register(() => Kill(process));
        var errors = ReadBoundedErrorsAsync(process.StandardError);
        int decoded = 0;
        try
        {
            while (true)
            {
                var pixels = new byte[checked(size.Width * size.Height * 4)];
                int read = 0;
                while (read < pixels.Length)
                {
                    int bytes = await process.StandardOutput.BaseStream.ReadAsync(pixels.AsMemory(read), ct);
                    if (bytes == 0) break;
                    read += bytes;
                }
                if (read == 0) break;
                if (read != pixels.Length) throw new InvalidDataException("FFmpeg returned an incomplete frame.");
                decoded++;
                yield return new(size.Width, size.Height, pixels);
            }
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0 && !(hardware && !info.HasAlpha && decoded == 0)) throw new InvalidDataException("Video decoding failed: " + await errors);
        }
        finally { Kill(process); }
        if (hardware && !info.HasAlpha && decoded == 0)
            await foreach (var frame in DecodeFramesAsync(path, info, start, maxHeight, false, timing, count, ct)) yield return frame;
    }
    public async Task<RgbaFrame> ReadFrameAsync(string path, VideoInfo info, double time, int maxHeight = 1080, CancellationToken ct = default)
    {
        time = Math.Clamp(time, 0, Math.Max(0, info.Duration - 1 / Math.Max(1, info.Fps)));
        await foreach (var frame in DecodeAsync(path, info, time, Math.Min(info.Duration, time + 1 / Math.Max(1, info.Fps) + .002), Math.Max(1, info.Fps), maxHeight, true, ct)) return frame;
        throw new InvalidDataException("No video frame could be decoded at this position.");
    }
    internal static async Task<string> ReadBoundedErrorsAsync(StreamReader reader)
    {
        var log = new StringBuilder();
        var chars = new char[2048]; int read;
        while ((read = await reader.ReadAsync(chars)) > 0) { log.Append(chars, 0, read); if (log.Length > 16000) log.Remove(0, log.Length - 16000); }
        return log.ToString();
    }
    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }
    public Process StartWebmEncoder(string output, SizeI size, ProjectDocument project)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", $"{size.Width}x{size.Height}", "-framerate", Number(project.Fps), "-i", "pipe:0" };
        if (project.Export.IncludeAudio) args.AddRange(["-ss", Number(project.TrimStart), "-t", Number(project.TrimEnd - project.TrimStart), "-i", project.SourcePath, "-map", "0:v:0", "-map", "1:a:0?", "-c:a", "libopus", "-b:a", "128k"]);
        else args.Add("-an");
        int crf = project.Export.VideoQuality switch { VideoQuality.High => 16, VideoQuality.Balanced => 28, VideoQuality.Small => 38, _ => 0 };
        args.AddRange(["-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-b:v", "0", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-deadline", "good", "-cpu-used", "2", "-row-mt", "1", "-threads", Math.Clamp(Environment.ProcessorCount / 2, 2, 16).ToString(CultureInfo.InvariantCulture), "-auto-alt-ref", "0", "-metadata:s:v:0", "alpha_mode=1"]);
        if (project.Export.VideoQuality == VideoQuality.Lossless) args.AddRange(["-lossless", "1"]);
        args.AddRange(["-frames:v", FrameGeometry.FrameCount(project.TrimStart, project.TrimEnd, project.Fps).ToString(CultureInfo.InvariantCulture), "-f", "webm", output]);
        return Start(FfmpegPath, args, true);
    }

    internal Process StartPackedMp4Encoder(string output, SizeI size, ProjectDocument project, bool hasAudio)
    {
        var encoded = PackedAlphaVideo.EncodedSize(size);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo",
            "-pixel_format", "rgb24", "-video_size", $"{encoded.Width}x{encoded.Height}", "-framerate", Number(project.Fps), "-i", "pipe:0" };
        if (project.Export.IncludeAudio && hasAudio)
            args.AddRange(["-ss", Number(project.TrimStart), "-t", Number(project.TrimEnd - project.TrimStart), "-i", project.SourcePath,
                "-map", "0:v:0", "-map", "1:a:0?", "-c:a", "aac", "-b:a", "128k"]);
        else args.Add("-an");

        string crf = project.Export.VideoQuality switch { VideoQuality.Balanced => "20", VideoQuality.Small => "24", _ => "16" };
        args.AddRange(["-c:v", "libx264", "-preset", "medium", "-crf", crf, "-filter_threads", "2",
            "-vf", "scale=out_color_matrix=bt709", "-pix_fmt", "yuv420p", "-colorspace", "bt709",
            "-color_primaries", "bt709", "-color_trc", "bt709", "-movflags", "+faststart",
            "-metadata", "comment=" + PackedAlphaVideo.Comment(size), "-f", "mp4", output]);
        return Start(FfmpegPath, args, true);
    }
}

public interface IFrameProcessor : IDisposable
{
    RgbaFrame Process(RgbaFrame source, KeyerSettings keyer, IReadOnlyList<PolygonMask> masks, double time, RectI crop, SizeI output);
}
