using BgRecorder.Core.Data;
using BgRecorder.Core.Session;
using BgRecorder.Core.Storage;

namespace BgRecorder.Session;

/// <summary>
/// Disk-safety gate for the staging volume: floor check before a recording arms and a
/// low-space watchdog while one runs. Floor = max(10 GiB, 2x the rolling average match
/// video size), defaulting to 10 GiB when there is no history yet.
/// </summary>
public sealed class DiskSafety : IDiskSafety
{
    /// <summary>Hard minimum free-space floor (10 GiB).</summary>
    public const long MinimumFloorBytes = 10L << 30;

    /// <summary>How many of the newest matches feed the rolling average.</summary>
    private const int RollingWindowSize = 20;

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);

    private readonly string _stagingDir;
    private readonly IMatchRepository _repository;
    private readonly IFreeSpaceProbe _probe;
    private readonly TimeSpan _pollInterval;

    public DiskSafety(
        string stagingDir,
        IMatchRepository repository,
        IFreeSpaceProbe? probe = null,
        TimeSpan? watchdogPollInterval = null)
    {
        _stagingDir = stagingDir;
        _repository = repository;
        _probe = probe ?? new DriveFreeSpaceProbe();
        _pollInterval = watchdogPollInterval ?? DefaultPollInterval;
    }

    public ArmCheckResult CheckCanArm()
    {
        var floor = ComputeFloorBytes();
        long free;
        try
        {
            free = _probe.GetAvailableFreeBytes(_stagingDir);
        }
        catch (Exception ex)
        {
            return new ArmCheckResult(false, $"Could not determine free space on the staging volume: {ex.Message}");
        }
        return free >= floor
            ? new ArmCheckResult(true, null)
            : new ArmCheckResult(false, $"Free space {FormatGb(free)} is below the {FormatGb(floor)} safety floor on the staging volume.");
    }

    public IDisposable StartWatchdog(Action onLowSpace)
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var task = Task.Run(async () =>
        {
            try
            {
                var floor = ComputeFloorBytes();
                using var timer = new PeriodicTimer(_pollInterval);
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    long free;
                    try
                    {
                        free = _probe.GetAvailableFreeBytes(_stagingDir);
                    }
                    catch
                    {
                        continue; // transient probe failure; try again next tick
                    }
                    if (free < floor)
                    {
                        onLowSpace();
                        return; // fires once; the coordinator finalizes and re-checks before the next arm
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // watchdog stopped with the recording
            }
        }, CancellationToken.None);
        return new WatchdogHandle(cts, task);
    }

    /// <summary>
    /// Floor = max(10 GiB, 2x rolling average of the newest recorded match video sizes).
    /// Repository failures degrade to the 10 GiB minimum rather than blocking recording.
    /// </summary>
    internal long ComputeFloorBytes()
    {
        try
        {
            var matches = _repository.ListMatchesAsync().GetAwaiter().GetResult();
            var sizes = matches
                .Where(m => m.VideoSizeBytes is > 0)
                .OrderByDescending(m => m.StartedAt)
                .Take(RollingWindowSize)
                .Select(m => m.VideoSizeBytes!.Value)
                .ToList();
            if (sizes.Count == 0)
            {
                return MinimumFloorBytes;
            }
            return Math.Max(MinimumFloorBytes, 2 * (long)sizes.Average());
        }
        catch
        {
            return MinimumFloorBytes;
        }
    }

    private static string FormatGb(long bytes) => $"{bytes / (double)(1L << 30):0.#} GB";

    private sealed class WatchdogHandle(CancellationTokenSource cts, Task task) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            cts.Cancel();
            try
            {
                task.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // the loop only ever exits by cancellation or its single fire; nothing to surface
            }
            cts.Dispose();
        }
    }
}
