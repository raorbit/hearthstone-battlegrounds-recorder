using BgRecorder.Core.Session;
using BgRecorder.Storage;
using Serilog;

namespace BgRecorder.App;

/// <summary>
/// Runs a retention <see cref="StorageEngine.EnforceAsync"/> pass after each match finalizes (the
/// Finalizing → non-Finalizing transition, when the row is committed and no recording is active) and
/// once at startup. Passes never overlap, but a trigger that arrives mid-pass is coalesced and run
/// afterwards rather than dropped. A failure is logged and retried on the next finalize; disposal
/// cancels any in-flight pass. Contention-avoidance with an active recording is a follow-up —
/// enforcement runs in the Armed idle window today.
/// </summary>
internal sealed class StorageEnforcer : IDisposable
{
    private readonly StorageEngine _engine;
    private readonly ISessionCoordinator _coordinator;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private CoordinatorState _previousState;
    private bool _running;
    private bool _pending;
    private Task? _runTask;

    public StorageEnforcer(StorageEngine engine, ISessionCoordinator coordinator)
    {
        _engine = engine;
        _coordinator = coordinator;
        _previousState = coordinator.State;
        coordinator.StateChanged += OnStateChanged;
    }

    /// <summary>Request an enforcement pass now (e.g. at startup); coalesced if one is already running.</summary>
    public void TriggerEnforce()
    {
        lock (_gate)
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }
            _pending = true;
            if (_running)
            {
                return; // the active runner will pick up the pending request when it drains
            }
            _running = true;
            _runTask = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_pending)
                {
                    _running = false;
                    return;
                }
                _pending = false;
            }

            try
            {
                var report = await _engine.EnforceAsync(_cts.Token).ConfigureAwait(false);
                if (report.MovesExecuted > 0 || report.DeletesExecuted > 0 || report.RecordingBelowFloor)
                {
                    Log.Information(
                        "Retention: {Moves} moved, {Deletes} deleted, belowFloor={BelowFloor}",
                        report.MovesExecuted, report.DeletesExecuted, report.RecordingBelowFloor);
                }
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _running = false;
                }
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Storage enforcement pass failed; will retry after the next finalize");
            }
        }
    }

    private void OnStateChanged(CoordinatorState state)
    {
        bool finalized;
        lock (_gate)
        {
            finalized = _previousState == CoordinatorState.Finalizing && state != CoordinatorState.Finalizing;
            _previousState = state;
        }

        if (finalized)
        {
            TriggerEnforce();
        }
    }

    public void Dispose()
    {
        _coordinator.StateChanged -= OnStateChanged;
        _cts.Cancel();
        try
        {
            _runTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort: the pass is cancelled; nothing to surface at shutdown.
        }
        _cts.Dispose();
    }
}
