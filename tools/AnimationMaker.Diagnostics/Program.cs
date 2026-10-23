using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AnimationMaker.Core;
using AnimationMaker.Gpu;
using SkiaSharp;

string root = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/validation"); Directory.CreateDirectory(root);
var ffmpeg = new Ffmpeg();
const int width = 1280, height = 720, frameCount = 90; const double fps = 30;
string clip = Path.Combine(root, "demo.mp4");
Console.WriteLine("Creating a synthetic 3-second green-screen fixture…");
using (var encoder = Ffmpeg.Start(ffmpeg.FfmpegPath, ["-v", "error", "-y", "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", $"{width}x{height}", "-framerate", "30", "-i", "pipe:0", "-an", "-c:v", "libx264", "-crf", "12", "-preset", "veryfast", "-pix_fmt", "yuv420p", clip], true))
{
    var errors = encoder.StandardError.ReadToEndAsync();
    for (int i = 0; i < frameCount; i++) await encoder.StandardInput.BaseStream.WriteAsync(Demo(width, height, i / fps).Pixels);
    encoder.StandardInput.Close(); await encoder.WaitForExitAsync(); if (encoder.ExitCode != 0) throw new IOException(await errors);
}
var info = await ffmpeg.ProbeAsync(clip);
var project = new ProjectDocument { SourcePath = clip, Fps = fps, TrimEnd = 3, Export = new() { MaxFrameHeight = 512 } };
await ProjectStore.SaveAsync(Path.Combine(root, "demo.animproj"), project);
using var gpu = new GpuFrameProcessor();
var source = Demo(1920, 1080, .5); var timings = new List<double>();
for (int i = 0; i < 80; i++)
{
    var watch = Stopwatch.StartNew(); gpu.Render(source, new(), [], i / fps, new(0, 0, 1920, 1080), new(1920, 1080));
    // A readback forces completion, so these measurements include transfer back to RAM.
    _ = gpu.Readback(); watch.Stop(); if (i >= 10) timings.Add(watch.Elapsed.TotalMilliseconds);
}
var benchmark = new { adapter = gpu.AdapterName, source = "1920x1080", output = "1920x1080", samples = timings.Count, medianMilliseconds = timings.Order().ElementAt(timings.Count / 2), meanMilliseconds = timings.Average(), framesPerSecond = 1000 / timings.Average(), processMemoryMiB = Process.GetCurrentProcess().WorkingSet64 / (1024d * 1024), includesUploadAndReadback = true };
Console.WriteLine(JsonSerializer.Serialize(benchmark, ProjectStore.JsonOptions));
await File.WriteAllTextAsync(Path.Combine(root, "benchmark.json"), JsonSerializer.Serialize(benchmark, ProjectStore.JsonOptions));
var first = await ffmpeg.ReadFrameAsync(clip, info, .5, 16384);
Imaging.SavePng(Path.Combine(root, "source.png"), first);
Imaging.SavePng(Path.Combine(root, "keyed.png"), gpu.Process(first, project.Keyer, [], .5, new(0, 0, width, height), project.OutputSize(info)));
var service = new ExportService(ffmpeg, () => new GpuFrameProcessor());
var progress = new Progress<ExportProgress>(p => { if (p.Completed % 30 == 0 || p.Completed == p.Total) Console.WriteLine($"{p.Stage}: {p.Completed}/{p.Total}"); });
string atlasPath = Path.Combine(root, "demo_atlas");
if (!Directory.Exists(atlasPath)) await service.ExportAtlasAsync(project, info, atlasPath, progress);
await service.ExportWebmAsync(project, info, Path.Combine(root, "demo_alpha.webm"), progress);
Console.WriteLine("Smoke test complete: " + root);

static RgbaFrame Demo(int width, int height, double time)
{
    using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    using var canvas = new SKCanvas(bitmap); canvas.Clear(new SKColor(0, 255, 0));
    float s = width / 1280f; canvas.Scale(s); canvas.Translate(640, 360);
    using var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(255, 255, 255, 150), StrokeWidth = 16, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12) };
    float pulse = 1 + .04f * MathF.Sin((float)time * 5); canvas.Scale(pulse);
    canvas.DrawCircle(0, 0, 162, glow);
    using var fill = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new(-55, -65), 280, [new SKColor(135, 99, 225), new SKColor(43, 35, 86), new SKColor(17, 17, 34)], [.0f, .58f, 1], SKShaderTileMode.Clamp) };
    canvas.DrawCircle(0, 0, 154, fill);
    using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(255, 224, 128), StrokeWidth = 4 };
    canvas.DrawCircle(0, 0, 160, ring); ring.Color = new(236, 216, 255, 180); ring.StrokeWidth = 1.5f; canvas.DrawCircle(0, 0, 143, ring);
    canvas.Save(); canvas.RotateDegrees((float)(time * 45));
    using var orbit = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new(255, 239, 202, 230), StrokeWidth = 2 };
    canvas.DrawOval(new SKRect(-246, -74, 246, 74), orbit); canvas.Restore();
    using var core = new SKPaint { IsAntialias = true, Color = new SKColor(255, 238, 187) };
    using var diamondBuilder = new SKPathBuilder(); diamondBuilder.MoveTo(0, -70); diamondBuilder.LineTo(49, 0); diamondBuilder.LineTo(0, 70); diamondBuilder.LineTo(-49, 0); diamondBuilder.Close(); using var diamond = diamondBuilder.Detach(); canvas.DrawPath(diamond, core);
    core.Color = new(126, 88, 61); using var innerBuilder = new SKPathBuilder(); innerBuilder.MoveTo(0, -46); innerBuilder.LineTo(30, 0); innerBuilder.LineTo(0, 46); innerBuilder.LineTo(-30, 0); innerBuilder.Close(); using var inner = innerBuilder.Detach(); canvas.DrawPath(inner, core);
    var random = new Random(42);
    using var particle = new SKPaint { IsAntialias = true };
    for (int i = 0; i < 42; i++)
    {
        double angle = random.NextDouble() * Math.Tau + time * (i % 2 == 0 ? .25 : -.2); float radius = (float)(190 + random.NextDouble() * 100);
        float x = MathF.Cos((float)angle) * radius, y = MathF.Sin((float)angle) * radius * .65f;
        byte alpha = (byte)(80 + 175 * (.5 + .5 * Math.Sin(time * 4 + i)));
        particle.Color = new(255, 255, 255, alpha); float r = (float)(1 + random.NextDouble() * 2.5);
        canvas.DrawCircle(x, y, r, particle);
    }
    canvas.Flush(); var bytes = new byte[width * height * 4]; Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length); return new(width, height, bytes);
}
