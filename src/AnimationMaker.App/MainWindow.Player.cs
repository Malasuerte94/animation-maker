using System.Diagnostics;
using System.Runtime.CompilerServices;
using AnimationMaker.Core;
using AnimationMaker.Gpu;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Vortice.WinUI;
using Windows.Storage.Pickers;
using VirtualKey = Windows.System.VirtualKey;

namespace AnimationMaker.App;

public sealed partial class MainWindow
{
    private bool _playerTabSelected, _playerUpdating, _playerPlaying;
    private GpuFrameProcessor? _playerGpu;
    private Task? _playerInitialization;
    private PlayerMedia? _playerMedia;
    private int _playerFrame;
    private long _playerRenderVersion;
    private readonly SemaphoreSlim _playerRenderGate = new(1, 1);
    private CancellationTokenSource _playerSourceCts = new();
    private CancellationTokenSource? _playerSeekCts, _playerPlaybackCts;
    private sealed record PlayerMedia(string Path, VideoInfo Info, IAnimationSource? Source, int FrameCount);
    private sealed class PlayerImage(RgbaFrame frame) : IAnimationSource
    {
        public AnimationInfo Info { get; } = new(frame.Width, frame.Height, 1, [1000]);
        public RgbaFrame ReadFrame(int index, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return frame; }
    }

    private void OnWorkspaceTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || PlayerWorkspace == null) return;
        StopPlayback(); StopPlayerPlayback(); _playerSeekCts?.Cancel();
        _playerTabSelected = WorkspaceTabs.SelectedIndex == 1;
        var editorVisibility = _playerTabSelected ? Visibility.Collapsed : Visibility.Visible;
        KeyingSidebar.Visibility = EditorPreview.Visibility = ExportSidebar.Visibility = EditorTimeline.Visibility = EditorCommands.Visibility = editorVisibility;
        PlayerWorkspace.Visibility = _playerTabSelected ? Visibility.Visible : Visibility.Collapsed;
        if (_playerTabSelected)
        {
            Title = "Player · Animation Maker";
            PresentPlayer();
            StatusLabel.Text = _playerMedia == null ? "Player · open an exported animation or drop its folder" : "Player · " + _playerMedia.Path;
        }
        else
        {
            Title = (_info == null ? "Editor" : System.IO.Path.GetFileName(_project.SourcePath)) + " · Animation Maker";
            RequestPreview(); StatusLabel.Text = "Editor · editing session preserved";
        }
    }

    private async void OnPlayerSurfaceLoaded(object sender, RoutedEventArgs e) => await Guard(EnsurePlayerGpuAsync);
    private Task EnsurePlayerGpuAsync() => _playerInitialization ??= InitializePlayerGpuAsync();
    private async Task InitializePlayerGpuAsync()
    {
        var gpu = await Task.Run(() => new GpuFrameProcessor());
        if (_closed) { gpu.Dispose(); return; }
        _playerGpu = gpu; _ffmpeg ??= new Ffmpeg();
        gpu.CreateSwapChain(PlayerPixelWidth, PlayerPixelHeight);
        using var panel = new ISwapChainPanelNative(PlayerSurface); panel.SetSwapChain(gpu.SwapChain).CheckError();
        PresentPlayer();
    }
    private int PlayerPixelWidth => Math.Max(1, (int)Math.Round(PlayerSurface.ActualWidth * PlayerSurface.CompositionScaleX));
    private int PlayerPixelHeight => Math.Max(1, (int)Math.Round(PlayerSurface.ActualHeight * PlayerSurface.CompositionScaleY));
    private void PresentPlayer()
    {
        if (_closed || _playerGpu == null || !_playerTabSelected) return;
        float zoom = 1;
        if (_playerGpu.OutputSize.Width > 0 && PlayerZoomChoice.SelectedIndex > 0)
        {
            float fit = Math.Min((float)PlayerPixelWidth / _playerGpu.OutputSize.Width, (float)PlayerPixelHeight / _playerGpu.OutputSize.Height);
            zoom = (PlayerZoomChoice.SelectedIndex switch { 1 => 1, 2 => 2, _ => 4 }) / Math.Max(.000001f, fit);
        }
        _playerGpu.Present(PlayerPixelWidth, PlayerPixelHeight, (PreviewBackground)Math.Max(0, PlayerBackgroundChoice.SelectedIndex), zoom);
    }
    private void OnPlayerViewChanged(object sender, object e) { if (!_updating) PresentPlayer(); }
    private void OnPlayerViewportSizeChanged(object sender, SizeChangedEventArgs e)
    { if (!_updating) { try { PresentPlayer(); } catch (Exception ex) { App.Log(ex); StatusLabel.Text = ex.Message; } } }

    private async void OnOpenPlayerFile(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var picker = new FileOpenPicker(); InitializePicker(picker);
        foreach (string extension in new[] { ".json", ".webm", ".mp4", ".mov", ".webp", ".png" }) picker.FileTypeFilter.Add(extension);
        if (await picker.PickSingleFileAsync() is { } file) await ShowPlayerAsync(file.Path);
    });
    private async void OnOpenPlayerFolder(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializePicker(picker);
        if (await picker.PickSingleFolderAsync() is { } folder) await ShowPlayerAsync(folder.Path);
    });

    private async Task ShowPlayerAsync(string path)
    {
        if (_exportCts != null) return;
        if (!_playerTabSelected && _info != null && !_showingExport && _atlas == null)
        { _settingsTimer?.Stop(); if (!CommitControls()) return; }
        WorkspaceTabs.SelectedIndex = 1;
        await EnsurePlayerGpuAsync();
        StopPlayerPlayback(); _playerSeekCts?.Cancel(); _playerSourceCts.Cancel(); _playerSourceCts.Dispose(); _playerSourceCts = new();
        var ct = _playerSourceCts.Token;
        StatusLabel.Text = "Opening export…";
        if (Directory.Exists(path))
        {
            string? manifest = new[] { "delta.animation.json", "animation.json" }.Select(name => System.IO.Path.Combine(path, name)).FirstOrDefault(File.Exists);
            path = manifest ?? throw new InvalidDataException("This folder has no delta.animation.json or animation.json. Open a video/image file directly instead.");
        }
        path = System.IO.Path.GetFullPath(path);
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        IAnimationSource? source = null; VideoInfo info;
        if (extension == ".json")
        {
            source = await Task.Run<IAnimationSource>(() =>
            {
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                return json.RootElement.TryGetProperty("format", out var format) && format.GetString() == DeltaAtlasExporter.Format
                    ? new DeltaAtlasPlayer(path) : new AtlasPlayer(path);
            }, ct);
            var a = source.Info; info = new(a.Width, a.Height, a.Duration, a.Fps, false, true);
        }
        else if (extension is ".webp" or ".png")
        {
            source = new PlayerImage(await Task.Run(() => Imaging.Load(path), ct));
            info = new(source.Info.Width, source.Info.Height, 1, 1, false, true);
        }
        else if (extension is ".webm" or ".mp4" or ".mov") info = await _ffmpeg!.ProbeAsync(path, ct);
        else throw new InvalidDataException("Open an animation JSON, WebM/MP4/MOV video or WebP/PNG image.");
        ct.ThrowIfCancellationRequested();
        int frameCount = source?.Info.FrameCount ?? FrameGeometry.FrameCount(0, info.Duration, info.Fps);
        if (frameCount <= 0) throw new InvalidDataException("The export contains no frames.");
        _playerMedia = new(path, info, source, frameCount); _playerFrame = 0;
        PlayerTitle.Text = System.IO.Path.GetFileName(path);
        string kind = source is DeltaAtlasPlayer ? "Delta animation" : source is AtlasPlayer ? "Spritesheet animation" : source is PlayerImage ? "Image / atlas page" : "Video";
        PlayerInfoLabel.Text = $"{kind}\n{info.Width} × {info.Height} px\n{frameCount} frames · {info.Fps:0.###} fps\n{info.Duration:0.###} seconds";
        if (source is DeltaAtlasPlayer delta)
        {
            var encoding = delta.Manifest.Encoding;
            PlayerInfoLabel.Text += $"\n{delta.Manifest.Pages.Count} pages · {delta.Manifest.Patches.Count} unique patches\n" +
                (encoding?.TextureFormat == "webp" ? $"WebP · quality {encoding.TextureQuality}/100" : "PNG") + $"\nPatch tolerance {encoding?.Tolerance ?? 0}/255";
            if (delta.Report is { } report) PlayerInfoLabel.Text += "\n\n" + DeltaSummary(report);
        }
        if (info.HasAudio) PlayerInfoLabel.Text += "\nSilent preview";
        _playerUpdating = true;
        try { PlayerPositionSlider.Maximum = Math.Max(1, frameCount - 1); PlayerFreezeFrame.Maximum = frameCount - 1; }
        finally { _playerUpdating = false; }
        PlayerTransport.IsEnabled = PlayerFreezeFrame.IsEnabled = true; PlayerEmptyState.Visibility = Visibility.Collapsed;
        await SeekPlayerAsync(0);
        StatusLabel.Text = "Player ready · " + path;
    }

    private async Task<RgbaFrame> ReadPlayerFrameAsync(PlayerMedia media, int index, CancellationToken ct) => media.Source != null
        ? await Task.Run(() => media.Source.ReadFrame(index, ct), ct)
        : await _ffmpeg!.ReadFrameAsync(media.Path, media.Info, index / media.Info.Fps, 16384, ct);

    private async Task DisplayPlayerFrameAsync(PlayerMedia media, int index, RgbaFrame frame, long version, CancellationToken ct)
    {
        await _playerRenderGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_closed || version != _playerRenderVersion || _playerMedia != media) return;
            await Task.Run(() => _playerGpu!.Render(frame, new() { Enabled = false }, [], index / media.Info.Fps,
                new(0, 0, frame.Width, frame.Height), new(frame.Width, frame.Height)), ct);
            ct.ThrowIfCancellationRequested();
            if (_closed || version != _playerRenderVersion || _playerMedia != media) return;
            _playerFrame = index; _playerUpdating = true;
            try { PlayerPositionSlider.Value = index; PlayerFreezeFrame.Value = index; }
            finally { _playerUpdating = false; }
            PlayerTimeLabel.Text = $"Frame {index} / {media.FrameCount - 1}  ·  {index / media.Info.Fps:0.000}s / {media.Info.Duration:0.000}s";
            PresentPlayer();
        }
        finally { _playerRenderGate.Release(); }
    }
    private async Task SeekPlayerAsync(int index)
    {
        StopPlayerPlayback();
        if (_playerMedia is not { } media || _closed) return;
        _playerSeekCts?.Cancel(); _playerSeekCts?.Dispose(); _playerSeekCts = CancellationTokenSource.CreateLinkedTokenSource(_playerSourceCts.Token);
        var ct = _playerSeekCts.Token; long version = Interlocked.Increment(ref _playerRenderVersion);
        index = Math.Clamp(index, 0, media.FrameCount - 1);
        var image = await ReadPlayerFrameAsync(media, index, ct);
        await DisplayPlayerFrameAsync(media, index, image, version, ct);
    }
    private async void OnPlayerPositionChanged(object sender, object e)
    { if (!_playerUpdating && _playerMedia != null) await Guard(() => SeekPlayerAsync((int)Math.Round(PlayerPositionSlider.Value))); }
    private async void OnPlayerFreezeFrameChanged(object sender, object e)
    { if (!_playerUpdating && _playerMedia != null && double.IsFinite(PlayerFreezeFrame.Value)) await Guard(() => SeekPlayerAsync((int)Math.Round(PlayerFreezeFrame.Value))); }
    private async void OnPlayerPreviousFrame(object sender, RoutedEventArgs e) => await Guard(() => SeekPlayerAsync(_playerFrame - 1));
    private async void OnPlayerNextFrame(object sender, RoutedEventArgs e) => await Guard(() => SeekPlayerAsync(_playerFrame + 1));
    private void StopPlayerPlayback()
    { _playerPlaybackCts?.Cancel(); _playerPlaying = false; if (PlayerPlayButton != null) PlayerPlayButton.Content = "▶  Play"; }

    private async void OnPlayerPlay(object sender, RoutedEventArgs e)
    {
        if (_playerPlaying) { StopPlayerPlayback(); return; }
        if (_playerMedia is not { } media || _playerGpu == null) return;
        _playerSeekCts?.Cancel(); _playerPlaybackCts?.Dispose(); _playerPlaybackCts = CancellationTokenSource.CreateLinkedTokenSource(_playerSourceCts.Token);
        var ct = _playerPlaybackCts.Token; long version = Interlocked.Increment(ref _playerRenderVersion);
        _playerPlaying = true; PlayerPlayButton.Content = "Ⅱ  Pause";
        await Guard(async () =>
        {
            try
            {
                int start = _playerFrame >= media.FrameCount - 1 ? 0 : _playerFrame;
                do
                {
                    double speed = PlayerSpeedChoice.SelectedIndex switch { 0 => .25, 1 => .5, 3 => 2, _ => 1 };
                    var clock = Stopwatch.StartNew(); int index = start;
                    async IAsyncEnumerable<RgbaFrame> frames([EnumeratorCancellation] CancellationToken token)
                    {
                        if (media.Source != null)
                            for (int i = start; i < media.FrameCount; i++) yield return await Task.Run(() => media.Source.ReadFrame(i, token), token);
                        else
                            await foreach (var image in _ffmpeg!.DecodeAsync(media.Path, media.Info, start / media.Info.Fps, media.Info.Duration, media.Info.Fps, 16384, false, token)) yield return image;
                    }
                    await foreach (var image in frames(ct))
                    {
                        double wait = (index - start) / (media.Info.Fps * speed) - clock.Elapsed.TotalSeconds;
                        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                        ct.ThrowIfCancellationRequested();
                        if (index >= media.FrameCount) break;
                        await DisplayPlayerFrameAsync(media, index++, image, version, ct);
                    }
                    double tail = (index - start) / (media.Info.Fps * speed) - clock.Elapsed.TotalSeconds;
                    if (tail > 0) await Task.Delay(TimeSpan.FromSeconds(tail), ct);
                    ct.ThrowIfCancellationRequested(); start = 0;
                } while (PlayerLoopToggle.IsChecked == true);
            }
            finally { if (_playerPlaybackCts?.Token == ct) { _playerPlaying = false; PlayerPlayButton.Content = "▶  Play"; } }
        });
    }

    private void HandlePlayerKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox || _playerMedia == null) return;
        if (e.Key == VirtualKey.Space) OnPlayerPlay(sender, new());
        else if (e.Key == VirtualKey.Left) OnPlayerPreviousFrame(sender, new());
        else if (e.Key == VirtualKey.Right) OnPlayerNextFrame(sender, new());
        else return;
        e.Handled = true;
    }
    private async void OnSavePixiRuntime(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var picker = new FileSavePicker { SuggestedFileName = "animation-maker-pixi-delta" };
        picker.FileTypeChoices.Add("PixiJS delta runtime", [".zip"]); InitializePicker(picker);
        if (await picker.PickSaveFileAsync() is not { } file) return;
        await DeltaRuntimePackage.SaveAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "delta-runtime"), file.Path);
        StatusLabel.Text = "PixiJS runtime saved · " + file.Path;
    });
    private void ClosePlayer()
    {
        StopPlayerPlayback(); _playerSourceCts.Cancel(); _playerSeekCts?.Cancel();
        if (_playerGpu != null) { using var panel = new ISwapChainPanelNative(PlayerSurface); panel.SetSwapChain(null); _playerGpu.Dispose(); }
    }
}
