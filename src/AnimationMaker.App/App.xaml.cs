using Microsoft.UI.Xaml;

namespace AnimationMaker.App;

public partial class App : Application
{
    private Window? _window;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { Log(e.Exception); };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { _window = new MainWindow(); _window.Activate(); }
        catch (Exception ex) { Log(ex); throw; }
    }
    internal static void Log(Exception error)
        => LogMessage(error.ToString());
    internal static void LogMessage(string message)
    {
        try { string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimationMaker"); Directory.CreateDirectory(folder); File.AppendAllText(Path.Combine(folder, "app.log"), $"{DateTimeOffset.Now:O} {message}\n"); } catch (IOException) { }
    }
}
