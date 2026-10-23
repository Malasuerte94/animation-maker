using AnimationMaker.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Storage.Pickers;

namespace AnimationMaker.App;

public sealed partial class MainWindow
{
    private string? _deltaPreviewDirectory;
    private void ClearDeltaPreview()
    {
        if (_deltaPreviewDirectory is not { } path) return;
        _deltaPreviewDirectory = null;
        // This is a directory created by this window, never a user-selected export path.
        try { Directory.Delete(path, true); } catch (IOException ex) { App.Log(ex); } catch (UnauthorizedAccessException ex) { App.Log(ex); }
    }
    private async void OnPreviewDelta(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_info == null || _atlas != null) return;
        StopPlayback(); _settingsTimer?.Stop(); if (!CommitControls()) return;
        // Preview uses the same encoder, runtime, quality settings and processed canvas as export.
        double start = Math.Clamp(_position, _project.TrimStart, Math.Max(_project.TrimStart, _project.TrimEnd - 1));
        var previewProject = _project with { TrimStart = start, TrimEnd = Math.Min(_project.TrimEnd, start + 1) };
        string previewRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimationMaker", "DeltaPreviews");
        Directory.CreateDirectory(previewRoot);
        string path = System.IO.Path.Combine(previewRoot, Guid.NewGuid().ToString("N"));
        ClearDeltaPreview(); _deltaPreviewDirectory = path;
        string? lastExport = _lastExport;
        try
        {
            await RunExportAsync(false, path, delta: true, exportProject: previewProject);
            if (!_closed && File.Exists(System.IO.Path.Combine(path, "delta.animation.json"))) await PlayLastExportAsync();
        }
        finally
        {
            _lastExport = lastExport;
            if (!_showingExport || _closed) ClearDeltaPreview();
            if (!_closed) UpdateEditingAvailability();
        }
    });

    private async void OnExportDelta(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_info == null || _atlas != null) return;
        StopPlayback(); _settingsTimer?.Stop(); if (!CommitControls()) return;
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializePicker(picker);
        if (await picker.PickSingleFolderAsync() is not { } folder) return;
        string name = System.IO.Path.GetFileNameWithoutExtension(_project.SourcePath) + "_delta";
        string path = System.IO.Path.Combine(folder.Path, name); int suffix = 2;
        while (Directory.Exists(path) || File.Exists(path)) path = System.IO.Path.Combine(folder.Path, name + "_" + suffix++);
        await RunExportAsync(false, path, delta: true);
    });

    private static string DeltaSummary(DeltaReport r) =>
        $"Delta BETA · {r.DeltaBytes / 1024d:0.#} KiB vs classic PNG {r.ClassicBytes / 1024d:0.#} KiB\n" +
        (r.SavingsPercent > 0 ? $"{r.SavingsPercent:0.#}% smaller" : $"{Math.Abs(r.SavingsPercent):0.#}% larger · classic recommended") +
        $" · {r.VerifiedFrames} frames verified\n{r.Pages} pages · {r.UniquePatches} patches · {r.AverageOperations:0.#} ops/frame (max {r.MaximumOperations})\n" +
        $"GPU: atlas {r.AtlasGpuBytes / 1048576d:0.##} MiB + {r.PerPlayerGpuBytes / 1048576d:0.##} MiB/player\n" +
        (r.TextureFormat == "webp" ? $"WebP {r.TextureQuality}/100 · patch tolerance {r.Tolerance}/255 · max visible error {r.MaximumVisibleError}/255 · mean {r.MeanVisibleError:0.###}/255" :
            r.Tolerance == 0 ? "Exact RGBA" : $"Approximate · tolerance {r.Tolerance}/255 · max visible error {r.MaximumVisibleError}/255 · mean {r.MeanVisibleError:0.###}/255") +
        $" · {r.UpdatedPixelPercent:0.#}% canvas updated/frame\n" +
        $"Runtime {r.RuntimeBytes / 1024d:0.#} KiB · demo {r.DemoBytes / 1024d:0.#} KiB separately" +
        (r.Performance is { } p ? $"\nDelta analysis: {p.EffectiveBackend} · export {p.TotalMilliseconds / 1000:0.##} s\n{p.BackendReason}" : "");

    private void OnChangedRegions(object sender, RoutedEventArgs e) => DrawOverlay();
    private void DrawDeltaRegions()
    {
        if (ChangedRegions.IsChecked != true || _atlas is not DeltaAtlasPlayer delta) return;
        int index = Math.Clamp((int)Math.Floor(_position * delta.Info.Fps + .00001), 0, delta.Info.FrameCount - 1);
        foreach (var op in delta.Manifest.Frames[index].Operations)
        {
            var a = ProjectPoint(new((double)op.X / delta.Info.Width, (double)op.Y / delta.Info.Height));
            var b = ProjectPoint(new((double)(op.X + op.Width) / delta.Info.Width, (double)(op.Y + op.Height) / delta.Info.Height));
            if (a == null || b == null) continue;
            var color = op.Kind == "clear" ? Windows.UI.Color.FromArgb(255, 255, 123, 132) : Windows.UI.Color.FromArgb(255, 178, 247, 105);
            var rect = new Rectangle { Width = Math.Max(0, b.Value.X - a.Value.X), Height = Math.Max(0, b.Value.Y - a.Value.Y), Stroke = new SolidColorBrush(color), StrokeThickness = 1.5, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(30, color.R, color.G, color.B)), IsHitTestVisible = false };
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(rect, a.Value.X); Microsoft.UI.Xaml.Controls.Canvas.SetTop(rect, a.Value.Y); MaskOverlay.Children.Add(rect);
        }
    }
}
