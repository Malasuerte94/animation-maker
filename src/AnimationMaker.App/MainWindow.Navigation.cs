using AnimationMaker.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace AnimationMaker.App;

public sealed partial class MainWindow
{
    private PreviewCamera _editorCamera, _playerCamera;
    private UIElement? _panSurface;
    private uint? _panPointer;
    private Point _lastPanPoint;

    private double FitScale(bool player)
    {
        var size = (player ? _playerGpu : _gpu)?.OutputSize ?? default;
        if (size.Width <= 0 || size.Height <= 0) return 1;
        return Math.Min((double)(player ? PlayerPixelWidth : PixelWidth) / size.Width,
                        (double)(player ? PlayerPixelHeight : PixelHeight) / size.Height);
    }

    private void SetCamera(bool player, PreviewCamera camera)
    {
        if (player) _playerCamera = camera; else _editorCamera = camera;
        var choice = player ? PlayerZoomChoice : ZoomChoice;
        int preset = camera.Pan != default ? -1 : camera.Scale switch { null => 0, 1 => 1, 2 => 2, 4 => 3, _ => -1 };
        bool updating = _updating;
        _updating = true;
        try
        {
            choice.PlaceholderText = $"{(camera.Scale ?? FitScale(player)) * 100:0.##}%";
            choice.SelectedIndex = preset;
        }
        finally { _updating = updating; }
    }

    private void OnZoomPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || sender is not ComboBox { SelectedIndex: >= 0 } choice) return;
        bool player = choice == PlayerZoomChoice;
        double? scale = choice.SelectedIndex switch { 1 => 1, 2 => 2, 3 => 4, _ => null };
        SetCamera(player, new(scale, default));
        if (player) PresentPlayer(); else Present();
    }

    private void OnPreviewWheel(object sender, PointerRoutedEventArgs e)
    {
        bool player = ReferenceEquals(sender, PlayerGestureSurface);
        if ((player ? _playerMedia == null : _info == null) || _cropPointerId != null || _dragVertex >= 0) return;
        var surface = (FrameworkElement)sender;
        var pointer = e.GetCurrentPoint(surface);
        if (pointer.Properties.IsHorizontalMouseWheel) return;
        var camera = player ? _playerCamera : _editorCamera;
        SetCamera(player, camera.ZoomAt(new(pointer.Position.X, pointer.Position.Y),
            new(surface.ActualWidth / 2, surface.ActualHeight / 2), FitScale(player), pointer.Properties.MouseWheelDelta));
        if (player) PresentPlayer(); else Present();
        e.Handled = true;
    }

    private bool BeginPan(UIElement surface, PointerRoutedEventArgs e, bool allowLeft)
    {
        if (_panPointer != null || _cropPointerId != null || _dragVertex >= 0) return false;
        var p = e.GetCurrentPoint(surface);
        if (!(p.Properties.IsMiddleButtonPressed || (allowLeft && p.Properties.IsLeftButtonPressed))) return false;
        if (!surface.CapturePointer(e.Pointer)) return false;
        _panPointer = e.Pointer.PointerId;
        _panSurface = surface;
        _lastPanPoint = p.Position;
        e.Handled = true;
        return true;
    }

    private void OnPlayerPanPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_playerMedia != null) BeginPan(PlayerGestureSurface, e, true);
    }

    private void OnPanMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(_panSurface, sender) || _panPointer != e.Pointer.PointerId) return;
        var p = e.GetCurrentPoint((UIElement)sender).Position;
        bool player = ReferenceEquals(sender, PlayerGestureSurface);
        var camera = player ? _playerCamera : _editorCamera;
        SetCamera(player, camera with { Pan = new(camera.Pan.X + p.X - _lastPanPoint.X, camera.Pan.Y + p.Y - _lastPanPoint.Y) });
        _lastPanPoint = p;
        if (player) PresentPlayer(); else Present();
        e.Handled = true;
    }

    private void OnPanReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(_panSurface, sender) || _panPointer != e.Pointer.PointerId) return;
        OnPanMoved(sender, e);
        OnPanEnded(sender, e);
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    private void OnPanEnded(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(_panSurface, sender) || _panPointer != e.Pointer.PointerId) return;
        _panPointer = null;
        _panSurface = null;
        e.Handled = true;
    }
}
