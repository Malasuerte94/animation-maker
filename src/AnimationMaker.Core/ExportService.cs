using System.Runtime.CompilerServices;

namespace AnimationMaker.Core;

public sealed class ExportService(Ffmpeg ffmpeg, Func<IFrameProcessor> processorFactory)
{
    public async Task<DeltaReport> ExportDeltaAtlasAsync(ProjectDocument project, VideoInfo info, string destination,
        IProgress<ExportProgress>? progress = null, CancellationToken ct = default, string? runtimeDirectory = null)
    {
        ct.ThrowIfCancellationRequested(); project.Validate();
        var profiler = new ExportProfiler(); IFrameProcessor processor;
        using (profiler.Measure("processor_initialization")) processor = processorFactory();
        using (processor)
        {
            if (processor is IProfiledFrameProcessor profiled) profiled.Profiler = profiler;
            int expected = FrameGeometry.FrameCount(project.TrimStart, project.TrimEnd, project.Fps);
            using var analyzer = new AdaptiveDeltaAnalyzer(project.Export.DeltaAnalysisBackend,
                processor is IDeltaAnalyzerProvider provider ? () => provider.CreateDeltaAnalyzer(profiler) : null, profiler, expected,
                status => progress?.Report(new(status, 0, expected)));
            return await DeltaAtlasExporter.ExportAsync(ProcessFramesCoreAsync(project, info, processor, profiler, ct), destination, project.Export,
                project.Fps, progress, expected, ct, runtimeDirectory, analyzer, profiler);
        }
    }
    public async IAsyncEnumerable<RgbaFrame> ProcessFramesAsync(ProjectDocument project, VideoInfo info, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var processor = processorFactory();
        await foreach (var frame in ProcessFramesCoreAsync(project, info, processor, null, ct)) yield return frame;
    }
    private async IAsyncEnumerable<RgbaFrame> ProcessFramesCoreAsync(ProjectDocument project, VideoInfo info, IFrameProcessor processor,
        ExportProfiler? profiler, [EnumeratorCancellation] CancellationToken ct)
    {
        project.Validate();
        if (project.TrimEnd > info.Duration + .001) throw new InvalidDataException("Out point exceeds the source duration.");
        var crop = FrameGeometry.ValidateCrop(project.Crop, info.Width, info.Height);
        var size = project.OutputSize(info);
        int index = 0;
        await using var decoder = ffmpeg.DecodeAsync(project.SourcePath, info, project.TrimStart, project.TrimEnd, project.Fps, 16384, true, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool moved;
            using (profiler?.Measure("decode_wait")) moved = await decoder.MoveNextAsync();
            if (!moved) break;
            ct.ThrowIfCancellationRequested();
            RgbaFrame frame;
            using (profiler?.Measure("frame_processing_inclusive"))
                frame = processor.Process(decoder.Current, project.Keyer, project.Masks, project.TrimStart + index++ / project.Fps, crop, size);
            yield return frame;
        }
    }
    public Task ExportAtlasAsync(ProjectDocument project, VideoInfo info, string destination, IProgress<ExportProgress>? progress = null, CancellationToken ct = default) =>
        AtlasExporter.ExportAsync(ProcessFramesAsync(project, info, ct), destination, project.Export, project.Fps, progress, FrameGeometry.FrameCount(project.TrimStart, project.TrimEnd, project.Fps), ct);

    public Task ExportWebmAsync(ProjectDocument project, VideoInfo info, string destination, IProgress<ExportProgress>? progress = null, CancellationToken ct = default) =>
        ExportVideoAsync(project, info, destination, false, progress, ct);

    public Task ExportPackedMp4Async(ProjectDocument project, VideoInfo info, string destination, IProgress<ExportProgress>? progress = null, CancellationToken ct = default) =>
        ExportVideoAsync(project, info, destination, true, progress, ct);

    private async Task ExportVideoAsync(ProjectDocument project, VideoInfo info, string destination, bool packedMp4,
        IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        project.Validate();
        string format = packedMp4 ? "MP4" : "WebM";
        string stage = packedMp4 ? "Encoding H.264 with alpha mask" : "Encoding VP9 + alpha";
        string temporary = Path.GetFullPath(destination) + ".staging-" + Guid.NewGuid().ToString("N") + (packedMp4 ? ".mp4" : ".webm");
        try
        {
            var size = project.OutputSize(info);
            var physicalSize = packedMp4 ? PackedAlphaVideo.EncodedSize(size) : size;
            byte[]? packedBuffer = packedMp4 ? new byte[checked(physicalSize.Width * physicalSize.Height * 3)] : null;
            var alphaSamples = new List<byte[]>();
            int count = 0, expected = FrameGeometry.FrameCount(project.TrimStart, project.TrimEnd, project.Fps);
            using (var encoder = packedMp4 ? ffmpeg.StartPackedMp4Encoder(temporary, size, project, info.HasAudio)
                : ffmpeg.StartWebmEncoder(temporary, size, project))
            {
                using var registration = ct.Register(() => Ffmpeg.Kill(encoder));
                var errors = Ffmpeg.ReadBoundedErrorsAsync(encoder.StandardError);
                var stdout = encoder.StandardOutput.ReadToEndAsync(ct);
                try
                {
                    await foreach (var frame in ProcessFramesAsync(project, info, ct))
                    {
                        byte[] pixels = frame.Pixels;
                        if (packedBuffer != null)
                        {
                            if (frame.Width != size.Width || frame.Height != size.Height)
                                throw new InvalidDataException("The processed frame does not match the export dimensions.");
                            PackedAlphaVideo.Pack(frame, packedBuffer);
                            pixels = packedBuffer;
                        }
                        try { await encoder.StandardInput.BaseStream.WriteAsync(pixels, ct); }
                        catch (IOException error)
                        {
                            ct.ThrowIfCancellationRequested();
                            Ffmpeg.Kill(encoder);
                            throw new IOException($"{format} encoding failed: {await errors}", error);
                        }
                        alphaSamples.Add(SampleAlpha(frame));
                        count++;
                        progress?.Report(new(stage, count, expected));
                    }
                    encoder.StandardInput.Close(); await encoder.WaitForExitAsync(ct);
                    if (encoder.ExitCode != 0) throw new IOException($"{format} encoding failed: " + await errors);
                    await stdout;
                }
                catch
                {
                    Ffmpeg.Kill(encoder);
                    await encoder.WaitForExitAsync();
                    throw;
                }
            }
            if (count != expected) throw new InvalidDataException($"Encoded {count} of {expected} frames.");
            progress?.Report(new(packedMp4 ? "Verifying alpha mask" : "Verifying alpha stream", 0, expected));
            var encoded = await ffmpeg.ProbeAsync(temporary, ct);
            bool hasExpectedAlpha = packedMp4 ? encoded.CodecName == "h264" && encoded.PackedAlphaSize == size : encoded.HasAlpha;
            if (!hasExpectedAlpha || encoded.Width != physicalSize.Width || encoded.Height != physicalSize.Height)
                throw new InvalidDataException("The encoded file did not preserve alpha or dimensions.");
            int verified = 0;
            var decodedFrames = packedMp4 ? ffmpeg.DecodeEncodedFramesAsync(temporary, encoded, ct)
                : ffmpeg.DecodeAsync(temporary, encoded, 0, count / project.Fps, project.Fps, 16384, false, ct);
            await foreach (var decoded in decodedFrames)
            {
                decoded.Validate();
                if (verified >= alphaSamples.Count) throw new InvalidDataException("The encoder produced additional frames.");
                var actualAlpha = SampleAlpha(decoded, packedMp4 ? size : null); var wantedAlpha = alphaSamples[verified];
                double alphaError = actualAlpha.Zip(wantedAlpha, (a, b) => Math.Abs(a - b)).Average();
                // RGBA <-> YUVA conversion can round alpha by one level even with lossless VP9.
                double allowedError = project.Export.VideoQuality switch
                {
                    VideoQuality.Lossless when !packedMp4 => 1,
                    VideoQuality.High or VideoQuality.Lossless => 16,
                    VideoQuality.Balanced => 24,
                    _ => 40
                };
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
    private static byte[] SampleAlpha(RgbaFrame frame, SizeI? packedSize = null)
    {
        var result = new byte[64];
        var size = packedSize ?? new SizeI(frame.Width, frame.Height);
        int columnOffset = packedSize.HasValue ? frame.Width / 2 : 0;
        int channel = packedSize.HasValue ? 0 : 3;
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
        {
            int px = (int)((x + .5) * size.Width / 8), py = (int)((y + .5) * size.Height / 8);
            result[y * 8 + x] = frame.Pixels[(py * frame.Width + px + columnOffset) * 4 + channel];
        }
        return result;
    }
}
