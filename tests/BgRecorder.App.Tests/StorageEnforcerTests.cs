using System.IO;
using BgRecorder.Core.Data;
using BgRecorder.Core.Session;
using BgRecorder.Core.Storage;
using BgRecorder.Storage;
using Xunit;

namespace BgRecorder.App.Tests;

/// <summary>
/// Concurrency contract of <see cref="StorageEnforcer"/>: passes never overlap, triggers during a
/// pass coalesce into exactly one follow-up, failures are non-fatal, and dispose cancels cleanly.
/// The engine is real; the gated match store gives each test precise control over pass timing —
/// a pass blocks inside EnforceAsync until the test releases it.
/// </summary>
public sealed class StorageEnforcerTests
{
    [Fact]
    public async Task A_finalizing_to_armed_transition_triggers_exactly_one_pass()
    {
        var store = new GatedMatchStore();
        using var enforcer = NewEnforcer(store, out var coordinator);

        coordinator.Raise(CoordinatorState.Finalizing);
        coordinator.Raise(CoordinatorState.Armed);

        store.Release();
        await store.WaitForCallsAsync(1);
        await Task.Delay(150); // give a hypothetical spurious second pass time to appear
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task Transitions_without_a_finalizing_edge_do_not_trigger()
    {
        var store = new GatedMatchStore();
        using var enforcer = NewEnforcer(store, out var coordinator);

        coordinator.Raise(CoordinatorState.Recording);
        coordinator.Raise(CoordinatorState.Armed);
        coordinator.Raise(CoordinatorState.GameNotFound);

        await Task.Delay(200);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Triggers_during_a_running_pass_coalesce_into_one_follow_up()
    {
        var store = new GatedMatchStore();
        using var enforcer = NewEnforcer(store, out _);

        enforcer.TriggerEnforce();
        await store.WaitForCallsAsync(1); // first pass is now blocked inside the store

        enforcer.TriggerEnforce();
        enforcer.TriggerEnforce();
        enforcer.TriggerEnforce(); // three triggers while running → ONE pending pass

        store.Release(); // finish pass 1
        store.Release(); // finish the single coalesced pass 2
        await store.WaitForCallsAsync(2);
        await Task.Delay(200); // any third pass would start in this window
        Assert.Equal(2, store.Calls);
    }

    [Fact]
    public async Task A_failing_pass_is_swallowed_and_the_next_trigger_runs_again()
    {
        var store = new GatedMatchStore { FailNextCall = true };
        using var enforcer = NewEnforcer(store, out _);

        enforcer.TriggerEnforce();
        store.Release();
        await store.WaitForCallsAsync(1); // threw inside the engine; enforcer must survive

        enforcer.TriggerEnforce();
        store.Release();
        await store.WaitForCallsAsync(2); // and run again cleanly
    }

    [Fact]
    public async Task Dispose_cancels_a_blocked_pass_and_later_triggers_are_ignored()
    {
        var store = new GatedMatchStore();
        var enforcer = NewEnforcer(store, out _);

        enforcer.TriggerEnforce();
        await store.WaitForCallsAsync(1); // pass is parked on the gate, observing the token

        enforcer.Dispose(); // must cancel the wait and return promptly (bounded internally at 5s)

        enforcer.TriggerEnforce(); // post-dispose trigger must be a no-op
        await Task.Delay(200);
        Assert.Equal(1, store.Calls);
    }

    private static StorageEnforcer NewEnforcer(GatedMatchStore store, out FakeCoordinator coordinator)
    {
        coordinator = new FakeCoordinator();
        var fs = new NullFileSystem();
        var engine = new StorageEngine(
            store,
            new RetentionPolicy(),
            new ArchiveMover(fs, new NullJournal(), store),
            new BigFreeSpaceProbe(),
            fs,
            @"C:\lib",
            new StorageOptions());
        return new StorageEnforcer(engine, coordinator);
    }

    /// <summary>Blocks every ListMatchesAsync on a gate the test releases, honoring cancellation.</summary>
    private sealed class GatedMatchStore : IMatchStore
    {
        private readonly SemaphoreSlim _gate = new(0);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool FailNextCall { get; set; }

        public void Release() => _gate.Release();

        public async Task WaitForCallsAsync(int atLeast)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Calls < atLeast)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"never reached {atLeast} calls (got {Calls})");
                await Task.Delay(10);
            }
        }

        public async Task<IReadOnlyList<MatchRecord>> ListMatchesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            await _gate.WaitAsync(ct);
            if (FailNextCall)
            {
                FailNextCall = false;
                throw new IOException("simulated repository failure");
            }
            return [];
        }

        public Task DeleteMatchAsync(long matchId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateVideoLocationAsync(long matchId, string videoPath, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeCoordinator : ISessionCoordinator
    {
        public CoordinatorState State { get; private set; } = CoordinatorState.Armed;

        public event Action<CoordinatorState>? StateChanged;

        public event Action<string>? Diagnostic { add { } remove { } }

        public void Raise(CoordinatorState state)
        {
            State = state;
            StateChanged?.Invoke(state);
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopCurrentRecordingAsync() => Task.CompletedTask;

        public void PauseAutoRecording() { }

        public void ResumeNow() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullFileSystem : IFileSystem
    {
        public bool FileExists(string path) => false;

        public long GetFileSizeBytes(string path) => 0;

        public void CreateDirectoryForFile(string filePath) { }

        public Task CopyAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<string> ComputeContentHashAsync(string path, CancellationToken ct = default)
            => Task.FromResult("hash");

        public void Delete(string path) { }
    }

    private sealed class NullJournal : IMoverJournal
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<long> AppendAsync(MoverJournalEntry entry, CancellationToken ct = default)
            => Task.FromResult(1L);

        public Task UpdateStateAsync(long id, MoverJournalState state, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RemoveAsync(long id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MoverJournalEntry>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MoverJournalEntry>>([]);
    }

    private sealed class BigFreeSpaceProbe : IFreeSpaceProbe
    {
        public long GetAvailableFreeBytes(string path) => 500L << 30;
    }
}
