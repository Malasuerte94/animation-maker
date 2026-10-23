using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using AnimationMaker.Core;
using AnimationMaker.Gpu;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Vortice.WinUI;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Pickers;
using VirtualKey = Windows.System.VirtualKey;

namespace AnimationMaker.App;

public sealed partial class MainWindow : Window
{
    private bool _updating = true, _closed, _playing, _picking;
    private ProjectDocument _project = new();
    private EditHistory<ProjectDocument> _history = new(new());
    private VideoInfo? _info;
    private IAnimationSource? _atlas;
    private GpuFrameProcessor? _gpu;
    private Ffmpeg? _ffmpeg;
    private readonly FrameCache _cache = new();
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private CancellationTokenSource _sourceCts = new();
    private CancellationTokenSource? _previewCts, _playCts, _exportCts;
    private DispatcherQueueTimer? _settingsTimer;
    private RgbaFrame? _decoded;
    private double _position;
    private long _renderVersion;
    private List<PointD>? _draft;
    private int _editingMask = -1, _dragVertex = -1;
    private string? _projectPath, _lastExport;
    private string? _backgroundImagePath;
    private int _lastBackgroundChoice;
    private bool _showingExport;
    private EditorSnapshot? _originalEditor;
    private bool _startupOpened;

    private sealed record EditorSnapshot(ProjectDocument Project, EditHistory<ProjectDocument> History, VideoInfo Info,
        IAnimationSource? Atlas, double Position, string? ProjectPath, bool SourceView, int Resolution, int Zoom,
        int SelectedMask, List<PointD>? Draft, int EditingMask);

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1480, 940));
        AppWindow.TitleBar.BackgroundColor = Windows.UI.Color.FromArgb(255, 12, 17, 25);
        AppWindow.TitleBar.ForegroundColor = Colors.White;
        AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(255, 12, 17, 25);
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        _settingsTimer = DispatcherQueue.CreateTimer(); _settingsTimer.Interval = TimeSpan.FromMilliseconds(100); _settingsTimer.IsRepeating = false;
        _settingsTimer.Tick += (_, _) => CommitControls();
        Closed += OnClosed;
        _updating = false;
    }
    private async void OnPreviewLoaded(object sender, RoutedEventArgs e)
    {
        if (_gpu != null) return;
        await Guard(async () =>
        {
            StatusLabel.Text = "Initializing GPU…";
            _gpu = await Task.Run(() => new GpuFrameProcessor()); _ffmpeg = new Ffmpeg();
            _gpu.CreateSwapChain(PixelWidth, PixelHeight);
            using var panel = new ISwapChainPanelNative(PreviewSurface); panel.SetSwapChain(_gpu.SwapChain).CheckError();
            GpuLabel.Text = _gpu.AdapterName.Replace("NVIDIA GeForce ", ""); Present(); StatusLabel.Text = "Ready · drop an MP4 to begin";
            if (!_startupOpened)
            {
                _startupOpened = true; var args = Environment.GetCommandLineArgs(); int open = Array.IndexOf(args, "--open");
                if (open >= 0 && open + 1 < args.Length)
                {
                    if (args.Contains("--player")) await ShowPlayerAsync(args[open + 1]);
                    else await LoadPathAsync(args[open + 1]);
                }
            }
        });
    }
    private int PixelWidth => Math.Max(1, (int)Math.Round(PreviewSurface.ActualWidth * PreviewSurface.CompositionScaleX));
    private int PixelHeight => Math.Max(1, (int)Math.Round(PreviewSurface.ActualHeight * PreviewSurface.CompositionScaleY));
    private float ZoomFactor
    {
        get
        {
            if (_gpu == null || _gpu.OutputSize.Width == 0 || ZoomChoice.SelectedIndex <= 0) return 1;
            float fit = Math.Min((float)PixelWidth / _gpu.OutputSize.Width, (float)PixelHeight / _gpu.OutputSize.Height);
            return (ZoomChoice.SelectedIndex switch { 1 => 1, 2 => 2, _ => 4 }) / Math.Max(.000001f, fit);
        }
    }
    private void Present()
    {
        if (_closed) return;
        _gpu?.Present(PixelWidth, PixelHeight, (PreviewBackground)Math.Max(0, BackgroundChoice.SelectedIndex), ZoomFactor, (BackgroundImageFit)Math.Max(0, BackgroundFitChoice.SelectedIndex)); DrawOverlay();
    }
    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MaskOverlay != null) MaskOverlay.Clip = new RectangleGeometry { Rect = new(0, 0, Viewport.ActualWidth, Viewport.ActualHeight) };
        if (!_updating && _gpu != null) { try { Present(); } catch (Exception ex) { App.Log(ex); StatusLabel.Text = "Preview device error. Reopen the application: " + ex.Message; } }
    }
    private void OnViewChanged(object sender, object e)
    {
        if (_updating) return;
        if (ReferenceEquals(sender, BackgroundChoice))
        {
            if (BackgroundChoice.SelectedIndex == (int)PreviewBackground.Image && _backgroundImagePath == null)
            {
                SetBackgroundChoice(_lastBackgroundChoice);
                _ = Guard(ChooseBackgroundImageAsync);
                return;
            }
            _lastBackgroundChoice = Math.Max(0, BackgroundChoice.SelectedIndex);
            UpdateBackgroundControls();
        }
        if (ReferenceEquals(sender, ShowSource)) RequestPreview(); else Present();
    }
    private void SetBackgroundChoice(int index)
    {
        bool wasUpdating = _updating; _updating = true;
        try { BackgroundChoice.SelectedIndex = index; _lastBackgroundChoice = index; }
        finally { _updating = wasUpdating; }
        UpdateBackgroundControls();
    }
    private void UpdateBackgroundControls()
    {
        bool imageSelected = BackgroundChoice.SelectedIndex == (int)PreviewBackground.Image && _backgroundImagePath != null;
        BackgroundFitChoice.Visibility = imageSelected ? Visibility.Visible : Visibility.Collapsed;
        BackgroundImageDetails.Visibility = _backgroundImagePath != null ? Visibility.Visible : Visibility.Collapsed;
        BackgroundImageLabel.Text = _backgroundImagePath == null ? "" : "Preview background: " + System.IO.Path.GetFileName(_backgroundImagePath) + "\nExports keep transparency.";
    }
    private async void OnChooseBackgroundImage(object sender, RoutedEventArgs e) => await Guard(ChooseBackgroundImageAsync);
    private async Task ChooseBackgroundImageAsync()
    {
        if (_gpu == null) return;
        var picker = new FileOpenPicker(); InitializePicker(picker);
        foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }) picker.FileTypeFilter.Add(extension);
        if (await picker.PickSingleFileAsync() is not { } file) return;
        var frame = await Task.Run(() =>
        {
            var image = Imaging.Load(file.Path, applyOrientation: true);
            double scale = Math.Min(1, 4096d / Math.Max(image.Width, image.Height));
            return scale < 1 ? Imaging.Resize(image, Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale))) : image;
        });
        await Task.Run(() => _gpu.SetPreviewBackgroundImage(frame));
        _backgroundImagePath = file.Path; SetBackgroundChoice((int)PreviewBackground.Image); Present();
        StatusLabel.Text = "Photo background loaded · preview only · transparent exports are unchanged";
    }
    private void OnClearBackgroundImage(object sender, RoutedEventArgs e)
    {
        _gpu?.SetPreviewBackgroundImage(null); _backgroundImagePath = null;
        if (BackgroundChoice.SelectedIndex == (int)PreviewBackground.Image) SetBackgroundChoice((int)PreviewBackground.Checkerboard);
        else UpdateBackgroundControls();
        Present();
    }
    private void OnPreviewResolutionChanged(object sender, object e) { if (!_updating) { StopPlayback(); RequestPreview(); } }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Log(ex); if (_closed) return; StatusLabel.Text = ex.Message;
            try { await new ContentDialog { Title = "Animation Maker", Content = ex.Message, CloseButtonText = "OK", XamlRoot = Root.XamlRoot, RequestedTheme = ElementTheme.Dark }.ShowAsync(); } catch (InvalidOperationException) { }
        }
    }
    private void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
    private async void OnOpen(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var picker = new FileOpenPicker(); InitializePicker(picker);
        foreach (var extension in new[] { ".mp4", ".webm", ".mov", ".animproj", ".json" }) picker.FileTypeFilter.Add(extension);
        if (await picker.PickSingleFileAsync() is { } file)
        {
            if (System.IO.Path.GetExtension(file.Path).Equals(".json", StringComparison.OrdinalIgnoreCase)) await ShowPlayerAsync(file.Path);
            else await LoadPathAsync(file.Path);
        }
    });
    private void OnDragOver(object sender, DragEventArgs e) => e.AcceptedOperation = _exportCts == null ? DataPackageOperation.Copy : DataPackageOperation.None;
    private async void OnDrop(object sender, DragEventArgs e) => await Guard(async () =>
    {
        if (_exportCts != null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count > 0)
        {
            if (_playerTabSelected || System.IO.Path.GetExtension(items[0].Path).Equals(".json", StringComparison.OrdinalIgnoreCase) || Directory.Exists(items[0].Path)) await ShowPlayerAsync(items[0].Path);
            else await LoadPathAsync(items[0].Path);
        }
    });
    private void ResetSourceWork()
    {
        EndCropEditing();
        StopPlayback(); _settingsTimer?.Stop(); _sourceCts.Cancel(); _sourceCts.Dispose(); _sourceCts = new(); _previewCts?.Cancel();
        Interlocked.Increment(ref _renderVersion);
        _cache.Clear(); _decoded = null; _draft = null; _editingMask = -1; _picking = false; PickBanner.Visibility = Visibility.Collapsed;
    }
    private async Task LoadPathAsync(string path, bool exportPreview = false)
    {
        if (_gpu == null || _ffmpeg == null) throw new InvalidOperationException("GPU initialization has not completed.");
        ResetSourceWork();
        StatusLabel.Text = "Opening " + System.IO.Path.GetFileName(path) + "…";
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        ProjectDocument project; VideoInfo info; IAnimationSource? atlas = null; string? projectPath = null;
        if (extension == ".json")
        {
            atlas = await Task.Run<IAnimationSource>(() =>
            {
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                return json.RootElement.TryGetProperty("format", out var format) && format.GetString() == DeltaAtlasExporter.Format ? new DeltaAtlasPlayer(path) : new AtlasPlayer(path);
            });
            var a = atlas.Info; info = new(a.Width, a.Height, a.Duration, a.Fps, false, true);
            project = new() { SourcePath = path, TrimEnd = info.Duration, Fps = info.Fps, Keyer = new() { Enabled = false }, Export = new() { MaxFrameHeight = info.Height } };
        }
        else if (extension == ".animproj")
        {
            project = await ProjectStore.LoadAsync(path, _sourceCts.Token); info = await _ffmpeg.ProbeAsync(project.SourcePath, _sourceCts.Token); projectPath = path;
            FrameGeometry.ValidateCrop(project.Crop, info.Width, info.Height);
            if (project.TrimEnd > info.Duration + .001) throw new InvalidDataException("The project range exceeds the source duration.");
        }
        else
        {
            info = await _ffmpeg.ProbeAsync(path, _sourceCts.Token);
            project = new() { SourcePath = path, TrimEnd = info.Duration, Fps = Math.Clamp(info.Fps, 1, 240), Keyer = new() { Enabled = !info.HasAlpha } };
        }
        if (exportPreview) project = project with { Keyer = project.Keyer with { Enabled = false }, Export = project.Export with { MaxFrameHeight = info.Height } };
        _atlas = atlas; _info = info; _project = project; _history = new(project); _position = project.TrimStart; _projectPath = projectPath;
        _showingExport = exportPreview;
        if (!exportPreview) _originalEditor = null;
        if (exportPreview) { _updating = true; ShowSource.IsChecked = false; _updating = false; }
        UpdateMediaUi();
        SyncControls(); await RenderAtAsync(_position, _sourceCts.Token); _ = BuildFilmstripAsync(_sourceCts.Token);
        StatusLabel.Text = exportPreview ? "Export preview · Back to original (Esc) restores your editing session" : atlas == null ? "Clip ready · adjust the key, then export" : "Atlas player · original frame offsets restored";
        App.LogMessage($"Preview ready for {path} ({_gpu.OutputSize.Width}x{_gpu.OutputSize.Height})");
    }
    private void UpdateMediaUi()
    {
        if (_info == null) return;
        EmptyState.Visibility = Visibility.Collapsed;
        SourceTitle.Text = (_showingExport ? "EXPORT PREVIEW · " : "") + System.IO.Path.GetFileName(_project.SourcePath);
        Title = SourceTitle.Text + " · Animation Maker";
        MediaInfo.Text = $"{_info.Width} × {_info.Height}  ·  {_info.Fps:0.###} fps\n{_info.Duration:0.###} seconds" + (_showingExport ? "  ·  export preview" : _atlas != null ? "  ·  atlas player" : _info.HasAlpha ? "  ·  alpha video" : "  ·  source video");
        ExportPanel.IsEnabled = true;
        ChangedRegions.Visibility = _atlas is DeltaAtlasPlayer ? Visibility.Visible : Visibility.Collapsed;
        DeltaReportLabel.Text = _atlas is DeltaAtlasPlayer delta && delta.Report is { } report ? DeltaSummary(report) : "";
        UpdateEditingAvailability();
    }
    private void UpdateEditingAvailability()
    {
        bool busy = _exportCts != null;
        bool canEdit = _info != null && _atlas == null && !_showingExport && !busy;
        OpenButton.IsEnabled = !busy;
        WorkspaceTabs.IsEnabled = !busy;
        SaveButton.IsEnabled = KeyingPanel.IsEnabled = ExportSettingsPanel.IsEnabled = canEdit;
        AtlasExportButton.IsEnabled = DeltaExportButton.IsEnabled = DeltaPreviewButton.IsEnabled = WebmExportButton.IsEnabled = SetInButton.IsEnabled = SetOutButton.IsEnabled = canEdit;
        TimelinePanel.IsEnabled = _info != null && !busy;
        OpenExportButton.IsEnabled = !busy && _lastExport != null && !_showingExport;
        UndoButton.IsEnabled = canEdit && _history.CanUndo; RedoButton.IsEnabled = canEdit && _history.CanRedo;
        BackToOriginalButton.Visibility = BackToOriginalExportButton.Visibility = _originalEditor != null ? Visibility.Visible : Visibility.Collapsed;
        BackToOriginalButton.IsEnabled = BackToOriginalExportButton.IsEnabled = !busy;
    }

    private void OnSettingChanged(object sender, object e)
    {
        if (_updating || _info == null || _exportCts != null || _atlas != null || _showingExport) return;
        StopPlayback(); _settingsTimer?.Stop(); _settingsTimer?.Start();
    }
    private static double Valid(NumberBox box, double fallback) => double.IsFinite(box.Value) ? box.Value : fallback;
    private bool CommitControls()
    {
        if (_updating || _info == null || _atlas != null || _showingExport) return false;
        try
        {
            var keyer = _project.Keyer with { Enabled = KeyEnabled.IsOn, Mode = (KeyerMode)Math.Max(0, KeyMode.SelectedIndex), ScreenColor = ScreenColor.Text.Trim(), Similarity = (float)Similarity.Value, Softness = (float)Softness.Value, ClipBlack = (float)BlackClip.Value, ClipWhite = (float)WhiteClip.Value, Despill = (float)Despill.Value, RedWeight = (float)Valid(RedWeight, .5), BlueWeight = (float)Valid(BlueWeight, .5), Choke = (float)Valid(Choke, 0), Feather = (float)Valid(Feather, 0) };
            RectI? crop = CropEnabled.IsChecked == true ? new((int)Valid(CropX, 0), (int)Valid(CropY, 0), (int)Valid(CropWidth, _info.Width), (int)Valid(CropHeight, _info.Height)) : null;
            FrameGeometry.ValidateCrop(crop, _info.Width, _info.Height);
            var project = _project with { Keyer = keyer, Fps = Valid(FpsInput, _project.Fps), Crop = crop, Export = _project.Export with { MaxFrameHeight = (int)Valid(HeightInput, 512), MaxAtlasSize = AtlasSizeChoice.SelectedIndex switch { 0 => 2048, 2 => 8192, _ => 4096 }, Layout = (AtlasLayout)Math.Max(0, AtlasLayoutChoice.SelectedIndex), ImageFormat = (AtlasImageFormat)Math.Max(0, AtlasFormatChoice.SelectedIndex), WebpQuality = (int)Math.Round(Valid(WebpQualityInput, 75)), Deduplicate = Deduplicate.IsChecked == true, VideoQuality = (VideoQuality)Math.Max(0, VideoQualityChoice.SelectedIndex), IncludeAudio = IncludeAudio.IsChecked == true, DeltaTolerance = (int)Math.Round(Valid(DeltaToleranceInput, 8)) } };
            project.Validate(); Commit(project, false); return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or OverflowException) { StatusLabel.Text = ex.Message; return false; }
    }
    private void Commit(ProjectDocument project, bool sync = true)
    {
        _project = project; _history.Set(project); if (sync) SyncControls(); else UpdateLabels(); RequestPreview();
    }
    private void SyncControls()
    {
        if (_info == null) return;
        if (_cropEditing && _project.Crop == null) EndCropEditing();
        _updating = true;
        try
        {
            var k = _project.Keyer; var e = _project.Export;
            KeyEnabled.IsOn = k.Enabled; KeyMode.SelectedIndex = (int)k.Mode; ScreenColor.Text = k.ScreenColor;
            Similarity.Value = k.Similarity; Softness.Value = k.Softness; BlackClip.Value = k.ClipBlack; WhiteClip.Value = k.ClipWhite; Despill.Value = k.Despill;
            RedWeight.Value = k.RedWeight; BlueWeight.Value = k.BlueWeight; Choke.Value = k.Choke; Feather.Value = k.Feather;
            PlateLabel.Text = k.CleanPlatePath == null ? "Optional background image, without the subject" : System.IO.Path.GetFileName(k.CleanPlatePath);
            FpsInput.Value = _project.Fps; HeightInput.Value = e.MaxFrameHeight; CropEnabled.IsChecked = _project.Crop != null;
            var crop = _project.Crop ?? new(0, 0, _info.Width, _info.Height); CropX.Value = crop.X; CropY.Value = crop.Y; CropWidth.Value = crop.Width; CropHeight.Value = crop.Height;
            AtlasLayoutChoice.SelectedIndex = (int)e.Layout; AtlasSizeChoice.SelectedIndex = e.MaxAtlasSize switch { 2048 => 0, 8192 => 2, _ => 1 };
            AtlasFormatChoice.SelectedIndex = (int)e.ImageFormat; WebpQualityInput.Value = e.WebpQuality;
            DeltaToleranceInput.Value = e.DeltaTolerance; Deduplicate.IsChecked = e.Deduplicate; VideoQualityChoice.SelectedIndex = (int)e.VideoQuality; IncludeAudio.IsChecked = e.IncludeAudio; IncludeAudio.IsEnabled = _info.HasAudio;
            int selected = MaskList.SelectedIndex; MaskList.ItemsSource = _project.Masks; if (selected < _project.Masks.Count) MaskList.SelectedIndex = selected;
            PositionSlider.Maximum = Math.Max(.001, _info.Duration); PositionSlider.StepFrequency = 1 / _project.Fps; PositionSlider.Value = _position;
            UpdateLabels();
        }
        finally { _updating = false; }
    }
    private void UpdateLabels()
    {
        if (_info == null) return;
        var output = _project.OutputSize(_info);
        OutputSizeLabel.Text = $"{output.Width} × {output.Height} px / frame\n" + (_project.Crop == null ? "Full original canvas · no upscaling" : "Cropped canvas · no upscaling");
        FastControls.Visibility = _project.Keyer.Mode == KeyerMode.Fast ? Visibility.Visible : Visibility.Collapsed;
        Deduplicate.IsEnabled = _project.Export.Layout == AtlasLayout.Packed;
        WebpQualityInput.IsEnabled = _project.Export.ImageFormat == AtlasImageFormat.Webp;
        InLabel.Text = $"{_project.TrimStart:0.000}s"; OutLabel.Text = $"{_project.TrimEnd:0.000}s";
        UndoButton.IsEnabled = _history.CanUndo && _exportCts == null && !_showingExport; RedoButton.IsEnabled = _history.CanRedo && _exportCts == null && !_showingExport;
        TimeLabel.Text = $@"{TimeSpan.FromSeconds(_position):mm\:ss\.fff}  /  {TimeSpan.FromSeconds(_info.Duration):mm\:ss\.fff}";
        DrawOverlay();
    }
    private void RequestPreview()
    {
        if (_info == null || _gpu == null || _closed || _playing) return;
        _previewCts?.Cancel(); _previewCts?.Dispose(); _previewCts = CancellationTokenSource.CreateLinkedTokenSource(_sourceCts.Token);
        _ = Guard(() => RenderAtAsync(_position, _previewCts.Token));
    }
    private async Task<RgbaFrame> GetFrameAsync(double position, int height, CancellationToken ct)
    {
        if (_info == null || _ffmpeg == null) throw new InvalidOperationException("No source is open.");
        int index = Math.Clamp((int)Math.Floor(position * _project.Fps + .00001), 0, Math.Max(0, (int)Math.Ceiling(_info.Duration * _project.Fps) - 1));
        var atlas = _atlas; var info = _info; var path = _project.SourcePath;
        string key = $"{path}|{position:R}|{index}|{height}";
        if (_cache.Get(key) is { } cached) return cached;
        RgbaFrame frame = atlas != null ? await Task.Run(() => atlas.ReadFrame(index, ct), ct) : await _ffmpeg.ReadFrameAsync(path, info, position, height, ct);
        ct.ThrowIfCancellationRequested();
        _cache.Put(key, frame); return frame;
    }
    private async Task RenderAtAsync(double position, CancellationToken ct)
    {
        long version = Interlocked.Increment(ref _renderVersion);
        var source = await GetFrameAsync(position, PreviewResolution.SelectedIndex == 1 ? 16384 : 1080, ct);
        await _renderGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested(); if (version != _renderVersion || _closed) return;
            var project = _project; bool sourceView = ShowSource.IsChecked == true || _picking;
            bool cropEditing = _cropEditing;
            var crop = PreviewCrop(source); var size = PreviewOutputSize;
            await Task.Run(() => _gpu!.Render(source, sourceView ? project.Keyer with { Enabled = false } : project.Keyer, project.Masks, position, crop, size), ct);
            if (ct.IsCancellationRequested || version != _renderVersion || _closed) return;
            _decoded = source; _cropViewReady = cropEditing && _cropEditing; Present();
        }
        finally { _renderGate.Release(); }
    }
    private RectI PreviewCrop(RgbaFrame source)
    {
        if (_info == null) return new(0, 0, source.Width, source.Height);
        var crop = VisibleSourceRect;
        int x = Math.Clamp((int)Math.Round((double)crop.X * source.Width / _info.Width), 0, source.Width - 1);
        int y = Math.Clamp((int)Math.Round((double)crop.Y * source.Height / _info.Height), 0, source.Height - 1);
        int right = Math.Clamp((int)Math.Round((double)(crop.X + crop.Width) * source.Width / _info.Width), x + 1, source.Width);
        int bottom = Math.Clamp((int)Math.Round((double)(crop.Y + crop.Height) * source.Height / _info.Height), y + 1, source.Height);
        return new(x, y, right - x, bottom - y);
    }
    private async Task BuildFilmstripAsync(CancellationToken ct)
    {
        try
        {
            Filmstrip.Children.Clear(); if (_info == null || _ffmpeg == null) return;
            void add(RgbaFrame frame) => Filmstrip.Children.Add(new Image { Source = Bitmap(frame), Width = Math.Max(40, (Viewport.ActualWidth - 30) / 12 - 2), Height = 36, Stretch = Stretch.UniformToFill, Opacity = .8 });
            if (_atlas != null)
            {
                var atlas = _atlas;
                for (int i = 0; i < 12; i++) { ct.ThrowIfCancellationRequested(); var frame = await Task.Run(() => atlas.ReadFrame(i * atlas.Info.FrameCount / 12, ct), ct); if (!ct.IsCancellationRequested) add(Imaging.Resize(frame, Math.Max(1, (int)(36d * frame.Width / frame.Height)), 36)); }
            }
            else
                await foreach (var frame in _ffmpeg.DecodeAsync(_project.SourcePath, _info, 0, _info.Duration, 12 / _info.Duration, 48, true, ct)) { ct.ThrowIfCancellationRequested(); if (Filmstrip.Children.Count == 12) break; add(frame); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log(ex); }
    }
    private static WriteableBitmap Bitmap(RgbaFrame frame)
    {
        var bitmap = new WriteableBitmap(frame.Width, frame.Height); var bytes = new byte[frame.Pixels.Length];
        for (int i = 0; i < bytes.Length; i += 4) { int a = frame.Pixels[i + 3]; bytes[i] = (byte)(frame.Pixels[i + 2] * a / 255); bytes[i + 1] = (byte)(frame.Pixels[i + 1] * a / 255); bytes[i + 2] = (byte)(frame.Pixels[i] * a / 255); bytes[i + 3] = (byte)a; }
        using var stream = bitmap.PixelBuffer.AsStream(); stream.Write(bytes); bitmap.Invalidate(); return bitmap;
    }
    private void OnPositionChanged(object sender, object e)
    {
        if (_updating || _info == null) return; StopPlayback(); _position = Math.Clamp(PositionSlider.Value, 0, Math.Max(0, _info.Duration - 1 / _project.Fps)); UpdateLabels(); RequestPreview();
    }
    private void SetPosition(double position)
    {
        if (_info == null) return;
        _position = Math.Clamp(position, 0, Math.Max(0, _info.Duration - 1 / _project.Fps)); _updating = true; PositionSlider.Value = _position; _updating = false; UpdateLabels();
    }
    private void OnPreviousFrame(object sender, RoutedEventArgs e) { StopPlayback(); SetPosition(_position - 1 / _project.Fps); RequestPreview(); }
    private void OnNextFrame(object sender, RoutedEventArgs e) { StopPlayback(); SetPosition(_position + 1 / _project.Fps); RequestPreview(); }
    private void OnSetIn(object sender, RoutedEventArgs e) { StopPlayback(); if (_position < _project.TrimEnd - .5 / _project.Fps) Commit(_project with { TrimStart = _position }); }
    private void OnSetOut(object sender, RoutedEventArgs e) { StopPlayback(); if (_info != null && _position + 1 / _project.Fps > _project.TrimStart) Commit(_project with { TrimEnd = Math.Min(_info.Duration, _position + 1 / _project.Fps) }); }
    private void StopPlayback() { _playCts?.Cancel(); _playing = false; if (PlayButton != null) PlayButton.Content = "▶  Play"; }
    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_playing) { StopPlayback(); return; }
        if (_info == null || _gpu == null || _ffmpeg == null) return;
        if (_cropEditing) { _settingsTimer?.Stop(); if (!CommitControls()) return; }
        EndCropEditing();
        _previewCts?.Cancel(); _playCts?.Dispose(); _playCts = CancellationTokenSource.CreateLinkedTokenSource(_sourceCts.Token);
        var token = _playCts.Token; _playing = true; PlayButton.Content = "Ⅱ  Pause";
        await Guard(async () =>
        {
            try
            {
                do
                {
                    if (_position < _project.TrimStart || _position >= _project.TrimEnd - 1 / _project.Fps - .000001) SetPosition(_project.TrimStart);
                    double start = _position, speed = SpeedChoice.SelectedIndex switch { 0 => .25, 1 => .5, 3 => 2, _ => 1 };
                    int index = 0; var clock = Stopwatch.StartNew();
                    async Task display(RgbaFrame frame)
                    {
                        double time = start + index / _project.Fps;
                        double wait = index / (_project.Fps * speed) - clock.Elapsed.TotalSeconds;
                        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), token);
                        token.ThrowIfCancellationRequested();
                        await _renderGate.WaitAsync(token);
                        try
                        {
                            var crop = PreviewCrop(frame); var settings = ShowSource.IsChecked == true ? _project.Keyer with { Enabled = false } : _project.Keyer;
                            await Task.Run(() => _gpu.Render(frame, settings, _project.Masks, time, crop, _project.OutputSize(_info)), token);
                            token.ThrowIfCancellationRequested(); _decoded = frame; SetPosition(time); Present(); index++;
                        }
                        finally { _renderGate.Release(); }
                    }
                    if (_atlas != null)
                    {
                        var atlas = _atlas;
                        for (int frame = (int)Math.Round(start * _project.Fps); frame < atlas.Info.FrameCount; frame++) { var image = await Task.Run(() => atlas.ReadFrame(frame, token), token); await display(image); }
                    }
                    else
                        await foreach (var frame in _ffmpeg.DecodeAsync(_project.SourcePath, _info, start, _project.TrimEnd, _project.Fps, PreviewResolution.SelectedIndex == 1 ? 16384 : 1080, true, token)) await display(frame);
                    token.ThrowIfCancellationRequested(); if (LoopToggle.IsChecked == true) SetPosition(_project.TrimStart);
                } while (LoopToggle.IsChecked == true && !token.IsCancellationRequested);
            }
            finally { if (_playCts?.Token == token) { _playing = false; PlayButton.Content = "▶  Play"; } }
        });
    }

    private void OnUndo(object sender, RoutedEventArgs e) { if (_showingExport) return; FinishCropDrag(); StopPlayback(); _settingsTimer?.Stop(); _project = _history.Undo(); SyncControls(); RequestPreview(); }
    private void OnRedo(object sender, RoutedEventArgs e) { if (_showingExport) return; FinishCropDrag(); StopPlayback(); _settingsTimer?.Stop(); _project = _history.Redo(); SyncControls(); RequestPreview(); }
    private async void OnSaveProject(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_info == null || _atlas != null) return; _settingsTimer?.Stop(); if (!CommitControls()) return;
        var picker = new FileSavePicker { SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_project.SourcePath) }; picker.FileTypeChoices.Add("Animation Maker project", [".animproj"]); InitializePicker(picker);
        if (await picker.PickSaveFileAsync() is { } file) { await ProjectStore.SaveAsync(file.Path, _project); _projectPath = file.Path; StatusLabel.Text = "Project saved · " + file.Path; }
    });
    private void OnEyedropper(object sender, RoutedEventArgs e) { if (_decoded == null) return; EndCropEditing(); StopPlayback(); _picking = !_picking; PickBanner.Visibility = _picking ? Visibility.Visible : Visibility.Collapsed; RequestPreview(); }
    private void OnAutoSample(object sender, RoutedEventArgs e)
    {
        if (_decoded == null) return;
        _ = Guard(() => { Commit(_project with { Keyer = _project.Keyer with { ScreenColor = Imaging.SampleScreen(_decoded) } }); return Task.CompletedTask; });
    }
    private async void OnLoadPlate(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var picker = new FileOpenPicker(); InitializePicker(picker); foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".webp" }) picker.FileTypeFilter.Add(ext);
        if (await picker.PickSingleFileAsync() is { } file) { _ = Imaging.Load(file.Path); Commit(_project with { Keyer = _project.Keyer with { CleanPlatePath = file.Path } }); }
    });
    private void OnClearPlate(object sender, RoutedEventArgs e) => Commit(_project with { Keyer = _project.Keyer with { CleanPlatePath = null } });

    private void OnDrawMask(object sender, RoutedEventArgs e) { EndCropEditing(); StopPlayback(); _draft = []; _editingMask = -1; _picking = false; PickBanner.Visibility = Visibility.Collapsed; MaskHint.Text = "Click at least 3 vertices in the preview, then ✓. Drag vertices to adjust. Esc cancels."; RequestPreview(); DrawOverlay(); }
    private void OnEditMask(object sender, RoutedEventArgs e)
    {
        if (MaskList.SelectedIndex < 0) return; EndCropEditing(); StopPlayback(); _editingMask = MaskList.SelectedIndex; _draft = _project.Masks[_editingMask].At(_position).ToList(); MaskHint.Text = "Drag the vertices, then ✓ to save a keyframe at the current time."; RequestPreview(); DrawOverlay();
    }
    private void OnDeleteMask(object sender, RoutedEventArgs e)
    {
        int i = MaskList.SelectedIndex; if (i < 0) return; var masks = _project.Masks.Where((_, index) => index != i).ToList(); _draft = null; _editingMask = -1; Commit(_project with { Masks = masks });
    }
    private void OnApplyMask(object sender, RoutedEventArgs e)
    {
        if (_draft is not { Count: >= 3 }) { StatusLabel.Text = "A polygon needs at least three vertices."; return; }
        var masks = _project.Masks.ToList();
        if (_editingMask >= 0)
        {
            var mask = masks[_editingMask]; var keys = mask.Keyframes.Where(k => Math.Abs(k.Time - _position) > .000001).ToList(); keys.Add(new(_position, _draft.ToList())); masks[_editingMask] = mask with { Keyframes = keys.OrderBy(k => k.Time).ToList() };
        }
        else masks.Add(new() { Name = (MaskType.SelectedIndex == 0 ? "Keep " : "Remove ") + (masks.Count + 1), Kind = MaskType.SelectedIndex == 0 ? MaskKind.Keep : MaskKind.Remove, Keyframes = [new(_position, _draft.ToList())] });
        _draft = null; _editingMask = -1; Commit(_project with { Masks = masks }); MaskList.SelectedIndex = masks.Count - 1; MaskHint.Text = "Mask saved. Select it and use Edit / keyframe to animate its vertices.";
    }
    private void OnMaskSelection(object sender, object e) { if (!_updating) DrawOverlay(); }
    private Point? ProjectPoint(PointD p)
    {
        if (_info == null || _gpu == null || _gpu.OutputSize.Width == 0) return null;
        var crop = VisibleSourceRect;
        double fit = Math.Min(Viewport.ActualWidth / _gpu.OutputSize.Width, Viewport.ActualHeight / _gpu.OutputSize.Height) * ZoomFactor;
        double w = _gpu.OutputSize.Width * fit, h = _gpu.OutputSize.Height * fit;
        return new((Viewport.ActualWidth - w) / 2 + (p.X * _info.Width - crop.X) / crop.Width * w, (Viewport.ActualHeight - h) / 2 + (p.Y * _info.Height - crop.Y) / crop.Height * h);
    }
    private PointD? Unproject(Point point)
    {
        if (_info == null || _gpu == null || _gpu.OutputSize.Width == 0) return null;
        var crop = VisibleSourceRect;
        double fit = Math.Min(Viewport.ActualWidth / _gpu.OutputSize.Width, Viewport.ActualHeight / _gpu.OutputSize.Height) * ZoomFactor;
        double w = _gpu.OutputSize.Width * fit, h = _gpu.OutputSize.Height * fit;
        double x = (point.X - (Viewport.ActualWidth - w) / 2) / w, y = (point.Y - (Viewport.ActualHeight - h) / 2) / h;
        return x < 0 || y < 0 || x > 1 || y > 1 ? null : new((crop.X + x * crop.Width) / _info.Width, (crop.Y + y * crop.Height) / _info.Height);
    }
    private void OnPreviewPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (BeginCropDrag(e)) return;
        var point = e.GetCurrentPoint(MaskOverlay).Position; var normalized = Unproject(point); if (normalized == null) return;
        if (_picking && _decoded is { } frame)
        {
            int x = Math.Clamp((int)(normalized.Value.X * frame.Width), 0, frame.Width - 1), y = Math.Clamp((int)(normalized.Value.Y * frame.Height), 0, frame.Height - 1); int i = (y * frame.Width + x) * 4;
            _picking = false; PickBanner.Visibility = Visibility.Collapsed; Commit(_project with { Keyer = _project.Keyer with { ScreenColor = $"#{frame.Pixels[i]:X2}{frame.Pixels[i + 1]:X2}{frame.Pixels[i + 2]:X2}" } }); e.Handled = true; return;
        }
        if (_draft == null) return;
        for (int i = 0; i < _draft.Count; i++) if (ProjectPoint(_draft[i]) is { } projected && Math.Pow(projected.X - point.X, 2) + Math.Pow(projected.Y - point.Y, 2) < 144) { _dragVertex = i; MaskOverlay.CapturePointer(e.Pointer); e.Handled = true; return; }
        if (_editingMask < 0) _draft.Add(normalized.Value); DrawOverlay(); e.Handled = true;
    }
    private void OnPreviewPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_cropEditing) { UpdateCropDrag(e); return; }
        if (_draft != null && _dragVertex >= 0 && Unproject(e.GetCurrentPoint(MaskOverlay).Position) is { } normalized) { _draft[_dragVertex] = normalized; DrawOverlay(); e.Handled = true; }
    }
    private void OnPreviewPointerReleased(object sender, PointerRoutedEventArgs e) { if (_cropPointerId == e.Pointer.PointerId) { UpdateCropDrag(e); FinishCropDrag(); e.Handled = true; } _dragVertex = -1; MaskOverlay.ReleasePointerCapture(e.Pointer); }
    private void DrawOverlay()
    {
        if (MaskOverlay == null || _info == null) return; MaskOverlay.Children.Clear(); KeyframeMarkers.Children.Clear();
        void draw(IReadOnlyList<PointD> points, MaskKind kind, bool handles, bool draft)
        {
            if (points.Count == 0) return;
            var color = kind == MaskKind.Keep ? Windows.UI.Color.FromArgb(255, 178, 247, 105) : Windows.UI.Color.FromArgb(255, 255, 123, 132);
            var line = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = handles ? 2 : 1, Opacity = handles ? 1 : .45, IsHitTestVisible = false };
            foreach (var p in points) if (ProjectPoint(p) is { } xy) line.Points.Add(xy);
            if (points.Count > 2 && ProjectPoint(points[0]) is { } start) line.Points.Add(start);
            MaskOverlay.Children.Add(line);
            if (handles || draft) foreach (var p in points) if (ProjectPoint(p) is { } xy)
            {
                var dot = new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(color), Stroke = new SolidColorBrush(Colors.Black), StrokeThickness = 1, IsHitTestVisible = false }; Canvas.SetLeft(dot, xy.X - 4.5); Canvas.SetTop(dot, xy.Y - 4.5); MaskOverlay.Children.Add(dot);
            }
        }
        for (int i = 0; i < _project.Masks.Count; i++) if (_project.Masks[i].Enabled && (_draft == null || i != _editingMask)) draw(_project.Masks[i].At(_position), _project.Masks[i].Kind, i == MaskList.SelectedIndex, false);
        if (_draft != null) draw(_draft, _editingMask >= 0 ? _project.Masks[_editingMask].Kind : MaskType.SelectedIndex == 0 ? MaskKind.Keep : MaskKind.Remove, true, true);
        foreach (var key in _project.Masks.SelectMany(m => m.Keyframes)) { var marker = new Rectangle { Width = 3, Height = 6, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 178, 247, 105)) }; Canvas.SetLeft(marker, key.Time / _info.Duration * KeyframeMarkers.ActualWidth); KeyframeMarkers.Children.Add(marker); }
        DrawCropOverlay();
        DrawDeltaRegions();
    }

    private async void OnExportAtlas(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_info == null || _atlas != null) return; StopPlayback(); _settingsTimer?.Stop(); if (!CommitControls()) return;
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializePicker(picker); var folder = await picker.PickSingleFolderAsync(); if (folder == null) return;
        string name = System.IO.Path.GetFileNameWithoutExtension(_project.SourcePath) + "_atlas"; string path = System.IO.Path.Combine(folder.Path, name); int suffix = 2;
        while (Directory.Exists(path) || File.Exists(path)) path = System.IO.Path.Combine(folder.Path, name + "_" + suffix++);
        await RunExportAsync(false, path);
    });
    private async void OnExportWebm(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_info == null || _atlas != null) return; StopPlayback(); _settingsTimer?.Stop(); if (!CommitControls()) return;
        var picker = new FileSavePicker { SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_project.SourcePath) + "_alpha" }; picker.FileTypeChoices.Add("Transparent WebM", [".webm"]); InitializePicker(picker);
        if (await picker.PickSaveFileAsync() is { } file) await RunExportAsync(true, file.Path);
    });
    private async Task RunExportAsync(bool webm, string destination, bool delta = false, ProjectDocument? exportProject = null)
    {
        if (_ffmpeg == null || _info == null || _exportCts != null) return;
        EndCropEditing(); RequestPreview();
        var project = exportProject ?? _project; project.Validate(); var info = _info;
        _exportCts = new(); var ct = _exportCts.Token; SetExportBusy(true);
        try
        {
            var progress = new Progress<ExportProgress>(p => { ExportProgressBar.Value = p.Fraction * 100; ExportStage.Text = $"{p.Stage} · {p.Completed} / {p.Total}"; StatusLabel.Text = ExportStage.Text; });
            var service = new ExportService(_ffmpeg, () => new GpuFrameProcessor());
            DeltaReport? report = null;
            if (delta) report = await Task.Run(() => service.ExportDeltaAtlasAsync(project, info, destination, progress, ct), ct);
            else await Task.Run(() => webm ? service.ExportWebmAsync(project, info, destination, progress, ct) : service.ExportAtlasAsync(project, info, destination, progress, ct), ct);
            _lastExport = webm ? destination : System.IO.Path.Combine(destination, delta ? "delta.animation.json" : "animation.json"); StatusLabel.Text = "Export complete · " + destination;
            ExportStage.Text = report == null ? "Export complete · ready to play" : DeltaSummary(report); OpenExportButton.IsEnabled = true;
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Export cancelled · temporary files removed"; ExportStage.Text = "Cancelled"; }
        finally { _exportCts.Dispose(); _exportCts = null; if (!_closed) SetExportBusy(false); }
    }
    private void SetExportBusy(bool busy)
    {
        UpdateEditingAvailability();
        CancelExportButton.Visibility = ExportProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnCancelExport(object sender, RoutedEventArgs e) => _exportCts?.Cancel();
    private async void OnPlayExport(object sender, RoutedEventArgs e) => await Guard(async () => { if (_lastExport != null) await ShowPlayerAsync(_lastExport); });
    private async Task PlayLastExportAsync()
    {
        if (_lastExport == null || _exportCts != null || _showingExport || _info == null) return;
        StopPlayback(); _settingsTimer?.Stop();
        if (_atlas == null && !CommitControls()) return;
        EndCropEditing();
        _originalEditor ??= new(_project, _history, _info, _atlas, _position, _projectPath, ShowSource.IsChecked == true,
            PreviewResolution.SelectedIndex, ZoomChoice.SelectedIndex, MaskList.SelectedIndex, _draft?.ToList(), _editingMask);
        try { await LoadPathAsync(_lastExport, exportPreview: true); }
        catch
        {
            try { await ReturnToOriginalAsync(); } catch (Exception restoreError) { App.Log(restoreError); }
            throw;
        }
    }
    private async void OnBackToOriginal(object sender, RoutedEventArgs e) => await Guard(ReturnToOriginalAsync);
    private async Task ReturnToOriginalAsync()
    {
        if (_originalEditor is not { } original || _exportCts != null) return;
        if (!File.Exists(original.Project.SourcePath)) throw new FileNotFoundException("The original clip is missing. Restore it to return to this editing session.", original.Project.SourcePath);
        ResetSourceWork();
        _project = original.Project; _history = original.History; _info = original.Info; _atlas = original.Atlas;
        _position = original.Position; _projectPath = original.ProjectPath; _showingExport = false;
        _updating = true;
        try { ShowSource.IsChecked = original.SourceView; PreviewResolution.SelectedIndex = original.Resolution; ZoomChoice.SelectedIndex = original.Zoom; }
        finally { _updating = false; }
        UpdateMediaUi(); SyncControls();
        MaskList.SelectedIndex = original.SelectedMask; _draft = original.Draft?.ToList(); _editingMask = original.EditingMask;
        await RenderAtAsync(_position, _sourceCts.Token);
        _originalEditor = null; UpdateEditingAvailability(); _ = BuildFilmstripAsync(_sourceCts.Token);
        ClearDeltaPreview();
        StatusLabel.Text = "Original clip restored · settings, masks, undo history and timeline position preserved";
    }
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_playerTabSelected) { HandlePlayerKey(sender, e); return; }
        if (e.Key == VirtualKey.Escape && _cropEditing) { if (_cropDragStart != null) FinishCropDrag(cancel: true); else OnFinishCrop(sender, new()); e.Handled = true; return; }
        if (e.Key == VirtualKey.Enter && _cropEditing && e.OriginalSource is not TextBox) { OnFinishCrop(sender, new()); e.Handled = true; return; }
        if (e.Key == VirtualKey.Escape && _showingExport) { _ = Guard(ReturnToOriginalAsync); e.Handled = true; return; }
        if (e.Key == VirtualKey.Escape) { _draft = null; _editingMask = -1; _picking = false; PickBanner.Visibility = Visibility.Collapsed; DrawOverlay(); RequestPreview(); e.Handled = true; return; }
        if (e.OriginalSource is TextBox || _info == null || _exportCts != null) return;
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.Z) OnUndo(sender, new()); else if (ctrl && e.Key == VirtualKey.Y) OnRedo(sender, new());
        else if (e.Key == VirtualKey.Space) OnPlay(sender, new()); else if (e.Key == VirtualKey.Left) OnPreviousFrame(sender, new()); else if (e.Key == VirtualKey.Right) OnNextFrame(sender, new()); else return; e.Handled = true;
    }
    private void OnClosed(object sender, WindowEventArgs e)
    {
        _closed = true; StopPlayback(); _settingsTimer?.Stop(); _sourceCts.Cancel(); _previewCts?.Cancel(); _exportCts?.Cancel();
        if (_gpu != null) { using var panel = new ISwapChainPanelNative(PreviewSurface); panel.SetSwapChain(null); _gpu.Dispose(); }
        _cache.Clear();
        ClosePlayer();
        ClearDeltaPreview();
    }
}
