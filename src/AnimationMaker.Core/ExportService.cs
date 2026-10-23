using System.Runtime.CompilerServices;

namespace AnimationMaker.Core;

public sealed class ExportService(Ffmpeg ffmpeg, Func<IFrameProcessor> processorFactory)
{
    public Task<DeltaReport> ExportDeltaAtlasAsync(ProjectDocument project, VideoInfo info, string destination,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default, string? runtimeDirectory = null) =>
        DeltaAtlasExporter.ExportAsync(ProcessFramesAsync(project, info, ct), destination, project.Export, project.Fps,
            progress, FrameGeometry.FrameCount(project.TrimStart, project.TrimEnd, project.Fps), ct, runtimeDirectory);
    public async IAsyncEnumerable<RgbaFrame> ProcessFramesAsync(ProjectDocument project, VideoInfo info, [EnumeratorCancellation] CancellationToken ct = default)
    {
        project.Validate();
        if (project.TrimEnd > info.Duration + .001) throw new InvalidDataException("Out point exceeds the source duration.");
        var crop = FrameGeometry.ValidateCrop(project.Crop, info.Width, info.Height);
        var size = project.OutputSize(info);
        using var processor = processorFactory();
        int index = 0;
        await foreach (var source in ffmpeg.DecodeAsync(project.SourcePath, info, project.TrimStart, project.TrimEnd, project.Fps, 16384, true, ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return processor.Process(source, project.Keyer, project.Masks, project.TrimStart + index++ / project.Fps, crop, size);
        }
    }
    public Task ExportAtlasAsync(ProjectDocument project, VideoInfo info, string destination, IProgress<ExportProgress>? progress = null, CancellationToken ct = default) =>
        AtlasExporter.ExportAsync(ProcessFramesAsync(project, info, ct), destination, project.Export, project.Fps, progress, FrameGeometry.FrameCount(project.TrimStart, project.TrimEnd, project.Fps), ct);

    public async Task ExportWebmAsync(ProjectDocument project, VideoInfo info, string destination, IProgress<ExportProgress>? progress = null, CancellationToken ct = default)
    {
        project.Validate();
        string temporary = Path.GetFullPath(destination) + ".staging-" + Guid.NewGuid().ToString("N") + ".webm";
        try
        {
            var size = project.OutputSize(info);
            var alphaSamples = new List<byte[]>();
            int count = 0, expected = FrameGeometry.FrameCount(project.TrimStart, project.TrimEnd, project.Fps);
            using (var encoder = ffmpeg.StartWebmEncoder(temporary, size, project))
            {
                using var registration = ct.Register(() => Ffmpeg.Kill(encoder));
                var errors = Ffmpeg.ReadBoundedErrorsAsync(encoder.StandardError);
                var stdout = encoder.StandardOutput.ReadToEndAsync(ct);
                try
                {
                    await foreach (var frame in ProcessFramesAsync(project, info, ct))
                    {
                        await encoder.StandardInput.BaseStream.WriteAsync(frame.Pixels, ct);
                        alphaSamples.Add(SampleAlpha(frame));
                        count++;
                        progress?.Report(new("Encoding VP9 + alpha", count, expected));
                    }
                    encoder.StandardInput.Close(); await encoder.WaitForExitAsync(ct);
                    if (encoder.ExitCode != 0) throw new IOException("WebM encoding failed: " + await errors);
                    await stdout;
                }
                catch { Ffmpeg.Kill(encoder); throw; }
            }
            if (count != expected) throw new InvalidDataException($"Encoded {count} of {expected} frames.");
            progress?.Report(new("Verifying alpha stream", 0, 1));
            var encoded = await ffmpeg.ProbeAsync(temporary, ct);
            if (!encoded.HasAlpha || encoded.Width != size.Width || encoded.Height != size.Height) throw new InvalidDataException("The encoded file did not preserve alpha or dimensions.");
            int verified = 0;
            await foreach (var decoded in ffmpeg.DecodeAsync(temporary, encoded, 0, count / project.Fps, project.Fps, 16384, false, ct))
            {
                decoded.Validate();
                if (verified >= alphaSamples.Count) throw new InvalidDataException("The encoder produced additional frames.");
                var actualAlpha = SampleAlpha(decoded); var wantedAlpha = alphaSamples[verified];
                double alphaError = actualAlpha.Zip(wantedAlpha, (a, b) => Math.Abs(a - b)).Average();
                // RGBA <-> YUVA conversion can round alpha by one level even with lossless VP9.
                double allowedError = project.Export.VideoQuality switch { VideoQuality.Lossless => 1, VideoQuality.High => 16, VideoQuality.Balanced => 24, _ => 40 };
                if (alphaError > allowedError) throw new InvalidDataException($"Alpha verification failed on frame {verified}: average sampled error {alphaError:0.0}/255.");
                verified++;
                if (verified % 30 == 0) progress?.Report(new("Verifying encoded frames", verified, expected));
            }
            if (verified != count) throw new InvalidDataException($"Decoded {verified} of {count} encoded frames during verification.");
            ct.ThrowIfCancellationRequested(); File.Move(temporary, destination, true);
            progress?.Report(new("Export complete", expected, expected));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static byte[] SampleAlpha(RgbaFrame frame)
    {
        var result = new byte[64];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
        {
            int px = Math.Clamp((int)((x + .5) * frame.Width / 8), 0, frame.Width - 1), py = Math.Clamp((int)((y + .5) * frame.Height / 8), 0, frame.Height - 1);
            result[y * 8 + x] = frame.Pixels[(py * frame.Width + px) * 4 + 3];
        }
        return result;
    }
}
