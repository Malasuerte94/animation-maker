using AnimationMaker.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace AnimationMaker.App;

public sealed partial class MainWindow
{
    private bool _cropEditing, _cropViewReady;
    private int _cropPreviousZoom;
    private CropHandle _cropHandle;
    private PointD _cropAnchor;
    private ProjectDocument? _cropDragStart;
    private uint? _cropPointerId;

    private RectI VisibleSourceRect => _info == null ? default : _cropEditing
        ? new(0, 0, _info.Width, _info.Height) : FrameGeometry.ValidateCrop(_project.Crop, _info.Width, _info.Height);
    private SizeI PreviewOutputSize => _cropEditing && _info != null
        ? FrameGeometry.FitHeight(new(_info.Width, _info.Height), _project.Export.MaxFrameHeight) : _project.OutputSize(_info!);
    private CropViewport? CropView => _info == null || _gpu == null || _gpu.OutputSize.Width <= 0 || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0
        ? null : CropViewport.Fit(new(_info.Width, _info.Height), _gpu.OutputSize, Viewport.ActualWidth, Viewport.ActualHeight, ZoomFactor);

    private void OnCropEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_updating || _info == null || _atlas != null || _showingExport || _exportCts != null) return;
        StopPlayback(); _settingsTimer?.Stop();
        if (CropEnabled.IsChecked == true) BeginCropEditing();
        else { EndCropEditing(); CommitControls(); }
    }
    private void OnEditCrop(object sender, RoutedEventArgs e)
    {
        if (_cropEditing) OnFinishCrop(sender, e); else BeginCropEditing();
    }
    private void BeginCropEditing()
    {
        if (_info == null || _atlas != null || _showingExport || _exportCts != null || _cropEditing) return;
        StopPlayback(); _settingsTimer?.Stop();
        bool updating = _updating; _updating = true; CropEnabled.IsChecked = true; _updating = updating;
        if (!CommitControls()) return;
        _cropEditing = true; _cropViewReady = false; _cropPreviousZoom = ZoomChoice.SelectedIndex;
        _updating = true; ZoomChoice.SelectedIndex = 0; _updating = false;
        _draft = null; _editingMask = _dragVertex = -1; _picking = false; PickBanner.Visibility = Visibility.Collapsed;
        CropBanner.Visibility = Visibility.Visible; EditCropButton.Content = "Done cropping";
        StatusLabel.Text = "Crop · drag the handles to resize, inside to move, outside to draw · Esc finishes · Ctrl+Z undoes";
        RequestPreview();
    }
    private void OnFinishCrop(object sender, RoutedEventArgs e)
    {
        _settingsTimer?.Stop();
        if (!CommitControls()) return;
        EndCropEditing(); RequestPreview();
    }
    private void EndCropEditing()
    {
        FinishCropDrag();
        if (!_cropEditing) return;
        _cropEditing = _cropViewReady = false;
        bool updating = _updating; _updating = true; ZoomChoice.SelectedIndex = _cropPreviousZoom; _updating = updating;
        CropBanner.Visibility = Visibility.Collapsed; EditCropButton.Content = "Adjust on video";
        DrawOverlay();
    }
    private void OnResetCrop(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        FinishCropDrag(); StopPlayback(); _settingsTimer?.Stop();
        Commit(_project with { Crop = new(0, 0, _info.Width, _info.Height) });
        BeginCropEditing();
    }
    private bool BeginCropDrag(PointerRoutedEventArgs e)
    {
        if (!_cropEditing) return false;
        if (!_cropViewReady || _exportCts != null || CropView is not { } view) return true;
        var pointer = e.GetCurrentPoint(MaskOverlay);
        if (!pointer.Properties.IsLeftButtonPressed) return true;
        _settingsTimer?.Stop();
        if (!CommitControls()) return true;
        var point = new PointD(pointer.Position.X, pointer.Position.Y);
        var crop = _project.Crop ?? new RectI(0, 0, _info!.Width, _info.Height);
        var handle = CropGeometry.HitTest(crop, view, point);
        if (handle == CropHandle.None && !view.Contains(point)) return true;
        if (!MaskOverlay.CapturePointer(e.Pointer)) return true;
        _cropHandle = handle == CropHandle.None ? CropHandle.Draw : handle;
        _cropAnchor = view.Unproject(point); _cropDragStart = _project; _cropPointerId = e.Pointer.PointerId;
        if (_cropHandle == CropHandle.Draw) UpdateCropDrag(e);
        e.Handled = true; return true;
    }
    private void UpdateCropDrag(PointerRoutedEventArgs e)
    {
        if (_cropDragStart == null || _cropPointerId != e.Pointer.PointerId || CropView is not { } view || _info == null) return;
        var p = e.GetCurrentPoint(MaskOverlay).Position;
        var crop = CropGeometry.Drag(_cropDragStart.Crop ?? new(0, 0, _info.Width, _info.Height), _cropHandle,
            _cropAnchor, view.Unproject(new(p.X, p.Y)), new(_info.Width, _info.Height));
        _project = _cropDragStart with { Crop = crop };
        // Keep the complete canvas stable during the gesture; only the overlay and controls change.
        bool updating = _updating; _updating = true;
        CropEnabled.IsChecked = true; CropX.Value = crop.X; CropY.Value = crop.Y; CropWidth.Value = crop.Width; CropHeight.Value = crop.Height;
        _updating = updating; UpdateLabels(); e.Handled = true;
    }
    private void FinishCropDrag(bool cancel = false)
    {
        if (_cropDragStart == null) return;
        if (cancel) _project = _cropDragStart; else _history.Set(_project);
        _cropDragStart = null; _cropPointerId = null; _cropHandle = CropHandle.None;
        MaskOverlay.ReleasePointerCaptures(); SyncControls();
    }
    private void OnPreviewPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_cropPointerId == e.Pointer.PointerId) FinishCropDrag(cancel: true);
        _dragVertex = -1;
    }
    private void OnPreviewPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_cropPointerId == e.Pointer.PointerId) FinishCropDrag();
        _dragVertex = -1;
    }
    private void DrawCropOverlay()
    {
        if (!_cropEditing || !_cropViewReady || CropView is not { } view || _project.Crop is not { } crop) return;
        var a = view.Project(new(crop.X, crop.Y)); var b = view.Project(new(crop.X + crop.Width, crop.Y + crop.Height));
        void rectangle(double x, double y, double w, double h, Windows.UI.Color fill, bool outline = false)
        {
            if (w <= 0 || h <= 0) return;
            var shape = new Rectangle { Width = w, Height = h, Fill = new SolidColorBrush(fill), IsHitTestVisible = false };
            if (outline) { shape.Stroke = new SolidColorBrush(Colors.White); shape.StrokeThickness = 1.5; }
            Canvas.SetLeft(shape, x); Canvas.SetTop(shape, y); MaskOverlay.Children.Add(shape);
        }
        var shade = Windows.UI.Color.FromArgb(175, 0, 0, 0);
        rectangle(view.Left, view.Top, view.Width, a.Y - view.Top, shade);
        rectangle(view.Left, b.Y, view.Width, view.Top + view.Height - b.Y, shade);
        rectangle(view.Left, a.Y, a.X - view.Left, b.Y - a.Y, shade);
        rectangle(b.X, a.Y, view.Left + view.Width - b.X, b.Y - a.Y, shade);
        rectangle(a.X, a.Y, b.X - a.X, b.Y - a.Y, Colors.Transparent, true);
        for (int i = 1; i <= 2; i++)
        {
            var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 255, 255, 255));
            MaskOverlay.Children.Add(new Line { X1 = a.X + (b.X - a.X) * i / 3, X2 = a.X + (b.X - a.X) * i / 3, Y1 = a.Y, Y2 = b.Y, Stroke = brush, StrokeThickness = 1, IsHitTestVisible = false });
            MaskOverlay.Children.Add(new Line { X1 = a.X, X2 = b.X, Y1 = a.Y + (b.Y - a.Y) * i / 3, Y2 = a.Y + (b.Y - a.Y) * i / 3, Stroke = brush, StrokeThickness = 1, IsHitTestVisible = false });
        }
        foreach (var (_, position) in CropGeometry.Handles(crop))
        {
            var p = view.Project(position);
            rectangle(p.X - 5, p.Y - 5, 10, 10, Colors.White);
        }
        var label = new TextBlock { Text = $"{crop.Width} × {crop.Height} px", Foreground = new SolidColorBrush(Colors.White), FontSize = 12, IsHitTestVisible = false };
        Canvas.SetLeft(label, Math.Max(4, a.X + 10)); Canvas.SetTop(label, Math.Max(4, a.Y + 10)); MaskOverlay.Children.Add(label);
    }
}
