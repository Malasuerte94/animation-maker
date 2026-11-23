using System.Text.Json;
using AnimationMaker.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnimationMaker.App;

public sealed partial class MainWindow
{
    private static readonly string PreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimationMaker", "preferences.json");
    private EditorPreferences _preferences = new();

    private void LoadPreferences()
    {
        try { _preferences = EditorPreferences.Load(PreferencesPath); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { App.Log(ex); }
        PreviewResolution.SelectedIndex = _preferences.FullResolution ? 1 : 0;
        HeightInput.Value = _preferences.MaxFrameHeight;
        ProtectedColorList.ItemsSource = _preferences.ProtectedColors;
        ProtectToleranceInput.Value = _preferences.ProtectTolerance;
        ExportFormatChoice.SelectedIndex = (int)_preferences.ExportFormat;
        UpdateExportFormat();
    }

    private void SavePreferences(EditorPreferences next)
    {
        if (_preferences == next) return;
        try { next.Save(PreferencesPath); _preferences = next; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log(ex);
            StatusLabel.Text = "Could not save editor preferences · " + ex.Message;
        }
    }

    private void RememberEdits(ProjectDocument previous, ProjectDocument next)
    {
        var prefs = _preferences;
        if (previous.Export.MaxFrameHeight != next.Export.MaxFrameHeight) prefs = prefs with { MaxFrameHeight = next.Export.MaxFrameHeight };
        if (previous.Keyer.ProtectedColors != next.Keyer.ProtectedColors) prefs = prefs with { ProtectedColors = next.Keyer.ProtectedColors };
        if (previous.Keyer.ProtectTolerance != next.Keyer.ProtectTolerance) prefs = prefs with { ProtectTolerance = next.Keyer.ProtectTolerance };
        SavePreferences(prefs);
    }

    private void OnExportFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        UpdateExportFormat();
        SavePreferences(_preferences with { ExportFormat = (ExportKind)ExportFormatChoice.SelectedIndex });
    }

    private void UpdateExportFormat()
    {
        var kind = (ExportKind)ExportFormatChoice.SelectedIndex;
        bool mp4 = kind == ExportKind.PackedMp4;
        AtlasImageSettings.Visibility = kind is ExportKind.Atlas or ExportKind.Delta ? Visibility.Visible : Visibility.Collapsed;
        SpritesheetSettings.Visibility = kind == ExportKind.Atlas ? Visibility.Visible : Visibility.Collapsed;
        DeltaSettings.Visibility = kind == ExportKind.Delta ? Visibility.Visible : Visibility.Collapsed;
        VideoSettings.Visibility = kind is ExportKind.Webm or ExportKind.PackedMp4 ? Visibility.Visible : Visibility.Collapsed;
        WebmExportButton.Visibility = kind == ExportKind.Webm ? Visibility.Visible : Visibility.Collapsed;
        PackedMp4ExportButton.Visibility = PackedMp4Help.Visibility = mp4 ? Visibility.Visible : Visibility.Collapsed;
        // MP4 uses the High encoder preset for a project's WebM-only Lossless selection.
        LosslessQualityItem.Content = mp4 ? "High (WebM lossless preset)" : "Lossless";
        LosslessQualityItem.Visibility = mp4 && VideoQualityChoice.SelectedIndex != (int)VideoQuality.Lossless ? Visibility.Collapsed : Visibility.Visible;
    }
}
