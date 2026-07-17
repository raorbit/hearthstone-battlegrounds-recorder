using Velopack;

namespace BgRecorder.App;

/// <summary>
/// Explicit entry point so the Velopack hooks run before anything else: during install, update, and
/// uninstall, Velopack relaunches the app with hook arguments and expects it to handle them and exit
/// immediately — WPF must not start (nor logging, nor the single-instance mutex) on those runs.
/// Outside the hooks, <c>Run()</c> is a no-op and the normal tray app starts.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent(); // applies App.xaml (OnExplicitShutdown etc.)
        app.Run();
    }
}
