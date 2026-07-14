using System.IO;
using System.Text.Json;
using BgRecorder.Core;
using BgRecorder.Core.Audio;
using BgRecorder.Core.Capture;
using BgRecorder.Core.Data;
using BgRecorder.Core.Events;
using BgRecorder.Core.Session;
using Serilog;

// Concrete subsystems. Keeping every concrete-type reference in this one file means a
// signature change anywhere only ever has to be reconciled here.
using BgRecorder.Logs;
using BgRecorder.Capture;
using BgRecorder.Audio;
using BgRecorder.Audio.Muxing;
using BgRecorder.Data;
using BgRecorder.Session;

namespace BgRecorder.App;

/// <summary>
/// The single composition root: loads settings, runs onboarding, constructs every subsystem behind
/// its Core interface (concrete types appear only here), runs crash recovery, and starts the
/// coordinator. Deliberately the one place that knows the concrete class names and constructor shapes.
/// </summary>
internal static class CompositionRoot
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<AppServices> BuildAsync(CancellationToken ct)
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BgRecorder");
        Directory.CreateDirectory(appDataDir);

        var settingsPath = Path.Combine(appDataDir, "settings.json");
        var settings = LoadOrCreateSettings(settingsPath);

        RunOnboarding();

        Directory.CreateDirectory(settings.StagingDir);
        Directory.CreateDirectory(settings.LibraryDir);

        var installDir = settings.HearthstoneInstallDir ?? @"C:\Program Files (x86)\Hearthstone";
        var dbPath = Path.Combine(appDataDir, "library.db");

        IGameEventSource source = new GameEventSource(installDir);
        IRecorder recorder = new ScreenRecorderLibRecorder();
        IAudioCapture audio = new AudioCaptureEngine();
        IMuxer muxer = new MediaFoundationMuxer();

        IMatchRepository repository = new SqliteMatchRepository(dbPath);
        await repository.InitializeAsync(ct);
        Log.Information("Match repository initialized at {Db}", dbPath);

        IMatchAssembler assembler = new MatchAssembler();
        IDiskSafety diskSafety = new DiskSafety(settings.StagingDir, repository);
        IGameProcessLocator locator = new HearthstoneProcessLocator();

        var recovery = new StartupRecovery(muxer, assembler, repository, settings);
        var recoveryReport = await recovery.RunAsync(ct);
        Log.Information("Startup crash-recovery pass complete: {Count} staged session(s) examined", recoveryReport.Sessions.Count);
        foreach (var session in recoveryReport.Sessions)
        {
            Log.Information("Recovery {Dir}: {Outcome} ({Detail})", session.SessionDir, session.Outcome, session.Detail);
        }

        var coordinator = new SessionCoordinator(
            source, recorder, audio, muxer, assembler, repository, diskSafety, locator, settings);
        coordinator.Diagnostic += message => Log.Warning("Coordinator: {Message}", message);
        await coordinator.StartAsync(ct);
        Log.Information("Session coordinator started; initial state {State}", coordinator.State);

        return new AppServices
        {
            Settings = settings,
            Source = source,
            Coordinator = coordinator,
            LibraryDir = settings.LibraryDir,
        };
    }

    private static void RunOnboarding()
    {
        // Enable the Power logger in Hearthstone's log.config. The writer is merge-safe and a no-op
        // when the file is already compliant (which it is on this machine).
        var logConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Blizzard", "Hearthstone", "log.config");

        try
        {
            var result = LogConfigWriter.Ensure(logConfigPath);
            Log.Information("Onboarding: log.config ensure at {Path} -> {Result}", logConfigPath, result);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Onboarding: log.config ensure failed (non-fatal; recording still works if already set)");
        }
    }

    private static AppSettings LoadOrCreateSettings(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    Log.Information("Loaded settings from {Path}", path);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Settings load failed at {Path}; falling back to defaults", path);
        }

        var defaults = new AppSettings();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
            Log.Information("Wrote default settings to {Path}", path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not persist default settings to {Path}", path);
        }

        return defaults;
    }
}

/// <summary>
/// The composed, running application: its settings, the live coordinator, and the disposables that
/// must be torn down on exit. Disposal order is coordinator (stops any recording) then event source.
/// </summary>
internal sealed class AppServices : IAsyncDisposable
{
    public required AppSettings Settings { get; init; }
    public required IGameEventSource Source { get; init; }
    public required ISessionCoordinator Coordinator { get; init; }
    public required string LibraryDir { get; init; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Coordinator.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Coordinator dispose failed");
        }

        try
        {
            await Source.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Event source dispose failed");
        }
    }
}
