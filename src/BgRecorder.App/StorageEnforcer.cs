using BgRecorder.Core.Session;
using BgRecorder.Storage;
using Serilog;

namespace BgRecorder.App;

/// <summary>
/// Runs a retention <see cref="StorageEngine.EnforceAsync"/> pass after each match finalizes (the
/// Finalizing → non-Finalizing transition, when the row is committed and no recording is active) and
/// once at startup. Passes are non-overlapping and never fatal — a failure is logged and retried on
/// the next finalize. Contention-avoidance with an active recording is a follow-up; enforcement runs
/// in the Armed idle window today.
/// </summary>
internal sealed class StorageEnforcer : IDisposable
{
    private readonly StorageEngine _engine;
    private readonly ISessionCoordinator _coordinator;
    private CoordinatorState _previousState;
    private int _running;

    public StorageEnforcer(StorageEngine engine, ISessionCoordinator coordinator)
    {
        _engine = engine;
        _coordinator = coordinator;
        _previousState = coordinator.State;
        coordinator.StateChanged += OnStateChanged;
    }

    /// <summary>Kick off an enforcement pass now (e.g. at startup) if one is not already running.</summary>
    public void TriggerEnforce()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return; // a pass is already in flight
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var report = await _engine.EnforceAsync().ConfigureAwait(false);
                if (report.MovesExecuted > 0 || report.DeletesExecuted > 0 || report.RecordingBelowFloor)
                {
                    Log.Information(
                        "Retention: {Moves} moved, {Deletes} deleted, belowFloor={BelowFloor}",
                        report.MovesExecuted, report.DeletesExecuted, report.RecordingBelowFloor);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Storage enforcement pass failed; will retry after the next finalize");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    private void OnStateChanged(CoordinatorState state)
    {
        var previous = _previousState;
        _previousState = state;
        if (previous == CoordinatorState.Finalizing && state != CoordinatorState.Finalizing)
        {
            TriggerEnforce();
        }
    }

    public void Dispose() => _coordinator.StateChanged -= OnStateChanged;
}
