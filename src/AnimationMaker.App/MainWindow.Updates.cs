using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AnimationMaker.Core;
using Microsoft.UI.Xaml;

namespace AnimationMaker.App;

public sealed partial class MainWindow
{
    private static readonly HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromMinutes(2), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private static readonly string InstallId = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).ToUpperInvariant())));
    private static readonly string UpdateSessionName = "Local\\AnimationMaker." + InstallId;
    private readonly CancellationTokenSource _updateLifetime = new();
    private Mutex? _updateSession;
    private PreparedUpdate? _preparedUpdate;
    private bool _portableUpdate;

    private void StartUpdateChecks()
    {
        try
        {
            // Only published packages carry this marker. Development builds never install releases.
            var marker = Path.Combine(AppContext.BaseDirectory, "update-channel");
            if (!File.Exists(marker)) return;
            var mode = File.ReadAllText(marker).Trim();
            if (mode is not ("installed" or "portable")) return;
            _portableUpdate = mode == "portable";
            _updateSession = new Mutex(false, UpdateSessionName);
            if (Mutex.TryOpenExisting(UpdateSessionName + ".Installing", out var installing))
            {
                installing.Dispose();
                DispatcherQueue.TryEnqueue(Close);
                return;
            }
            string version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimationMaker", "updates", InstallId);
            var dispatcher = DispatcherQueue;
            _ = Task.Run(() => CheckForUpdatesAsync(new(UpdateHttp, cache), version, dispatcher, _updateLifetime.Token));
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private async Task CheckForUpdatesAsync(ApplicationUpdates updates, string version, Microsoft.UI.Dispatching.DispatcherQueue dispatcher, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                timeout.CancelAfter(TimeSpan.FromMinutes(20));
                var update = await updates.PrepareAsync(version, timeout.Token);
                if (update != null)
                {
                    dispatcher.TryEnqueue(() =>
                    {
                        if (_closed) return;
                        _preparedUpdate = update;
                        UpdateLabel.Text = $"{update.Tag} downloaded · installs on exit";
                        UpdateLabel.Visibility = Visibility.Visible;
                    });
                    return;
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
            catch (Exception ex) { App.LogMessage("Update check: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromHours(6), stopping); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ApplyUpdateOnExit()
    {
        _updateLifetime.Cancel();
        _updateSession?.Dispose();
        if (_preparedUpdate == null) return;
        try
        {
            if (Mutex.TryOpenExisting(UpdateSessionName, out var otherSession)) { otherSession.Dispose(); return; }
            _preparedUpdate.Start(AppContext.BaseDirectory, _portableUpdate, UpdateSessionName);
        }
        catch (Exception ex) { App.LogMessage("Update on exit: " + ex.Message); }
    }
}
