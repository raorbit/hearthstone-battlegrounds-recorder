using System.Diagnostics;
using BgRecorder.Core;
using BgRecorder.Core.Events;
using BgRecorder.Core.Session;
using BgRecorder.Session;

namespace BgRecorder.Session.Tests;

/// <summary>
/// Wires a SessionCoordinator to fakes and a per-test temp staging/library root,
/// records every StateChanged and Diagnostic, and offers wait helpers for the async loop.
/// </summary>
internal sealed class CoordinatorHarness : IAsyncDisposable
{
    public readonly string Root;
    public readonly AppSettings Settings;
    public readonly FakeGameEventSource Source = new();
    public readonly FakeRecorder Recorder = new();
    public readonly FakeAudioCapture Audio = new();
    public readonly FakeMuxer Muxer = new();
    public readonly FakeAssembler Assembler = new();
    public readonly FakeRepository Repository = new();
    public readonly FakeDiskSafety DiskSafety = new();
    public readonly FakeGameProcessLocator Locator = new();
    public readonly SessionCoordinator Coordinator;
    public readonly List<CoordinatorState> States = [];
    public readonly List<string> Diagnostics = [];

    public CoordinatorHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "bgrec-session-tests", Guid.NewGuid().ToString("N"));
        Settings = new AppSettings
        {
            StagingDir = Path.Combine(Root, "staging"),
            LibraryDir = Path.Combine(Root, "library"),
        };
        Coordinator = new SessionCoordinator(
            Source, Recorder, Audio, Muxer, Assembler, Repository, DiskSafety, Locator, Settings);
        Coordinator.StateChanged += s =>
        {
            lock (States)
            {
                States.Add(s);
            }
        };
        Coordinator.Diagnostic += m =>
        {
            lock (Diagnostics)
            {
                Diagnostics.Add(m);
            }
        };
    }

    public string StagingDir => Settings.StagingDir;

    public string LibraryDir => Settings.LibraryDir;

    public Task StartAsync() => Coordinator.StartAsync(CancellationToken.None);

    public CoordinatorState[] StateSequence()
    {
        lock (States)
        {
            return [.. States];
        }
    }

    public async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException(
                    $"Timed out waiting for {what ?? "condition"}. States so far: [{string.Join(", ", StateSequence())}]; " +
                    $"diagnostics: [{string.Join(" | ", Diagnostics)}]");
            }
            await Task.Delay(10);
        }
    }

    public Task WaitForStateAsync(CoordinatorState state, int timeoutMs = 5000)
        => WaitUntilAsync(() =>
        {
            lock (States)
            {
                return States.Contains(state);
            }
        }, timeoutMs, $"state {state}");

    public Task WaitForStateCountAsync(int count, int timeoutMs = 5000)
        => WaitUntilAsync(() =>
        {
            lock (States)
            {
                return States.Count >= count;
            }
        }, timeoutMs, $"{count} state changes");

    /// <summary>Round-trips the command loop, guaranteeing all previously posted commands ran.</summary>
    public Task DrainAsync() => Coordinator.StopCurrentRecordingAsync();

    /// <summary>Runs a standard BG match start (MatchStarted + GameTypeResolved) and waits for Recording.</summary>
    public async Task StartMatchAsync(BgGameType type = BgGameType.Solo)
    {
        Source.Raise(new MatchStarted(Ev.T0));
        Source.Raise(new GameTypeResolved(Ev.T0.AddSeconds(1), type));
        await WaitForStateAsync(CoordinatorState.Recording);
    }

    public string? SingleStagingSessionDir()
    {
        if (!Directory.Exists(StagingDir))
        {
            return null;
        }
        var dirs = Directory.GetDirectories(StagingDir);
        return dirs.Length == 1 ? dirs[0] : null;
    }

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // temp cleanup is best effort
        }
    }
}

/// <summary>Canonical event timestamps for tests.</summary>
internal static class Ev
{
    public static readonly DateTimeOffset T0 = new(2026, 7, 14, 20, 0, 0, TimeSpan.FromHours(-7));

    public static GameEvent[] FullMatch() =>
    [
        new MatchStarted(T0),
        new GameTypeResolved(T0.AddSeconds(1), BgGameType.Solo),
        new LocalHeroResolved(T0.AddSeconds(5), "BG31_HERO_001"),
        new TurnStarted(T0.AddSeconds(10), 1, 1),
        new TurnStarted(T0.AddMinutes(1), 2, 1),
        new CombatStarted(T0.AddMinutes(1), 1),
        new PlacementChanged(T0.AddMinutes(2), 7),
        new MatchEnded(T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false),
    ];
}
