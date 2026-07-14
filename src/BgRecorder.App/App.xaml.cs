using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using BgRecorder.Core.Session;
using Serilog;

namespace BgRecorder.App;

/// <summary>
/// Tray-only WPF shell and application lifecycle. All subsystem wiring lives in
/// <see cref="CompositionRoot"/>; this class owns the tray icon, command handlers,
/// the bootstrap try/catch, and the <c>--smoke</c> self-test path.
/// </summary>
public partial class App : Application
{
    private readonly CancellationTokenSource _cts = new();
    private TrayController? _tray;
    private AppServices? _services;
    private bool _isSmoke;
    private int _exitCode;
    private bool _shuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ConfigureLogging();
        _isSmoke = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase));
        Log.Information("BgRecorder starting (version {Version}, smoke={Smoke})",
            typeof(App).Assembly.GetName().Version, _isSmoke);

        try
        {
            _tray = new TrayController();
            _tray.StopRecordingRequested += OnStopRecording;
            _tray.PauseResumeRequested += OnPauseResume;
            _tray.OpenLibraryRequested += OnOpenLibrary;
            _tray.ExitRequested += () => RequestShutdown(0);
            _tray.Initialize();
        }
        catch (Exception ex)
        {
            // No interactive shell / desktop session: keep running headless and verify via logs.
            Log.Error(ex, "Tray icon could not be created; continuing without a tray glyph");
            _tray = null;
        }

        _ = BootstrapAsync();
    }

    private void ConfigureLogging()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BgRecorder", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "bgrecorder-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private async Task BootstrapAsync()
    {
        try
        {
            _services = await CompositionRoot.BuildAsync(_cts.Token);
            _services.Coordinator.StateChanged += OnCoordinatorStateChanged;
            ApplyState(_services.Coordinator.State);
            Log.Information("Bootstrap complete; coordinator state {State}", _services.Coordinator.State);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal bootstrap failure");
            _tray?.ShowFatalBalloon("BG Recorder failed to start", ex.Message);
            if (_isSmoke)
            {
                RequestShutdown(1);
            }
            // Non-smoke: stay alive so the user sees the balloon and can Exit from the tray menu.
            return;
        }

        if (_isSmoke)
        {
            Log.Information("Smoke mode: bootstrap succeeded, staying alive 8s then shutting down");
            await Task.Delay(TimeSpan.FromSeconds(8));
            Log.Information("Smoke mode: 8s elapsed, requesting clean shutdown");
            RequestShutdown(0);
        }
    }

    private void OnCoordinatorStateChanged(CoordinatorState state)
        => Dispatcher.InvokeAsync(() => ApplyState(state));

    private void ApplyState(CoordinatorState state)
    {
        Log.Information("Coordinator state -> {State}", state);
        _tray?.SetState(state);
    }

    private async void OnStopRecording()
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            Log.Information("User requested: stop this recording");
            await _services.Coordinator.StopCurrentRecordingAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Stop-this-recording failed");
        }
    }

    private void OnPauseResume()
    {
        if (_services is null)
        {
            return;
        }

        var coordinator = _services.Coordinator;
        try
        {
            if (coordinator.State == CoordinatorState.Paused)
            {
                Log.Information("User requested: resume now");
                coordinator.ResumeNow();
            }
            else
            {
                Log.Information("User requested: pause auto-recording");
                coordinator.PauseAutoRecording();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Pause/resume toggle failed");
        }
    }

    private void OnOpenLibrary()
    {
        var dir = _services?.LibraryDir ?? new Core.AppSettings().LibraryDir;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            Log.Information("Opened library folder {Dir}", dir);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open library folder {Dir}", dir);
        }
    }

    private void RequestShutdown(int exitCode)
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _exitCode = exitCode;
        Log.Information("Shutdown requested (exit code {Code})", exitCode);
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts.Cancel();

        try
        {
            // Dispose off the dispatcher thread with a bounded wait so a slow subsystem
            // teardown can never hang the shutdown or deadlock on the UI thread.
            var services = _services;
            if (services is not null)
            {
                _services = null;
                Task.Run(async () => await services.DisposeAsync()).Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disposing subsystems during shutdown");
        }

        try
        {
            _tray?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disposing tray icon during shutdown");
        }

        Environment.ExitCode = _exitCode;
        _cts.Dispose();
        Log.Information("BgRecorder exited with code {Code}", _exitCode);
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
