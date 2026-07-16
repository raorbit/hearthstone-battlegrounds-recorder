using BgRecorder.Core.Events;
using BgRecorder.Core.Rating;

namespace BgRecorder.Rating;

/// <summary>
/// <see cref="IRatingProvider"/> backed by the clean-room external Mono reader. Attaches lazily, samples the
/// live game on a throttle, projects the one read into per-mode snapshots, and — above all — never lets a
/// memory fault escape: any surprise degrades to a health state with a null snapshot, so recording is
/// wholly indifferent to it. Constructed only when <c>AppSettings.EnableMemoryRating</c> is on.
/// </summary>
/// <remarks>
/// Reading is <b>on demand</b>: a read happens when <see cref="TryGetAsync"/> is called (the throttle just
/// coalesces bursts). Capturing a per-match MMR <i>delta</i> — sample at match start, poll after the post-game
/// update, persist the difference on the match record — is a deliberate follow-up that belongs to the session
/// lifecycle (a coordinator-driven sampler plus a schema/DTO/SPA change), not to this reader.
/// </remarks>
public sealed class MemoryRatingProvider : IRatingProvider, IDisposable
{
    private readonly IProcessMemoryFactory _factory;
    private readonly MonoOffsets _offsets;
    private readonly Action<string> _diagnostic;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _pollThrottle;
    private readonly TimeSpan _reattachBackoff;
    private readonly TimeSpan _scanRetryBackoff;
    private readonly object _gate = new();

    private IProcessMemory? _memory;
    private BaconRatingReader? _reader;
    private bool _attached;
    private DateTimeOffset _lastAttachAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;
    private DateTimeOffset _nextScanAttempt = DateTimeOffset.MinValue;
    private RatingSnapshot? _lastSolo;
    private RatingSnapshot? _lastDuos;

    /// <summary>Production entry point: real attach, seed offset table, wall clock, sane throttles.</summary>
    public MemoryRatingProvider(Action<string> diagnostic)
        : this(
            new Win32ProcessMemoryFactory(),
            MonoOffsets.UnityMasterDefault,
            diagnostic,
            () => DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30))
    {
    }

    internal MemoryRatingProvider(
        IProcessMemoryFactory factory,
        MonoOffsets offsets,
        Action<string> diagnostic,
        Func<DateTimeOffset> now,
        TimeSpan pollThrottle,
        TimeSpan reattachBackoff,
        TimeSpan scanRetryBackoff)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _offsets = offsets ?? throw new ArgumentNullException(nameof(offsets));
        _diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _pollThrottle = pollThrottle;
        _reattachBackoff = reattachBackoff;
        _scanRetryBackoff = scanRetryBackoff;
    }

    public RatingHealth Health { get; private set; } = RatingHealth.AttachFailed;

    public Task<RatingSnapshot?> TryGetAsync(BgGameType mode, CancellationToken ct = default)
    {
        lock (_gate)
        {
            try
            {
                Poll(ct);
            }
            catch (Exception ex)
            {
                // Belt-and-suspenders over the already-total reads: the seam must never throw.
                _diagnostic($"unexpected fault: {ex.Message}");
                Health = RatingHealth.PatchBroken;
            }

            RatingSnapshot? snapshot = mode switch
            {
                BgGameType.Solo => _lastSolo,
                BgGameType.Duos => _lastDuos,
                _ => null,
            };
            return Task.FromResult(snapshot);
        }
    }

    private void Poll(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        DateTimeOffset now = _now();

        if (!_attached)
        {
            if (now - _lastAttachAttempt < _reattachBackoff)
            {
                return; // still backing off — serve the cached snapshot, keep the current health
            }

            _lastAttachAttempt = now;
            if (!_factory.TryAttach(out IProcessMemory memory, out AttachFault fault))
            {
                Health = MapAttachFault(fault);
                return;
            }

            _memory = memory;
            _reader = new BaconRatingReader(new MonoImageReader(memory, _offsets));
            _attached = true;
            // fall through to an immediate first poll
        }
        else if (now - _lastPoll < _pollThrottle)
        {
            return; // throttled — serve the cached snapshot
        }

        if (now < _nextScanAttempt)
        {
            // The last read died in the once-and-cached resolution stage, whose retry repeats the whole-domain
            // class scan (up to MonoOffsets.MaxScanReads reads). The class may still appear later — Mono builds
            // it lazily — so retry, but on this longer cadence rather than every poll.
            return;
        }

        _lastPoll = now;
        RatingReadResult result = _reader!.Read(ct);
        if (ct.IsCancellationRequested)
        {
            return; // a cancelled scan's result is not trustworthy — leave health/cache untouched
        }

        ApplyResult(result, now);
    }

    private void ApplyResult(RatingReadResult result, DateTimeOffset now)
    {
        switch (result.State)
        {
            case RatingReadState.Ok:
                Health = RatingHealth.Ok;
                _lastSolo = result.Rating > 0 ? new RatingSnapshot(BgGameType.Solo, result.Rating, now) : null;
                _lastDuos = result.DuosRating > 0 ? new RatingSnapshot(BgGameType.Duos, result.DuosRating, now) : null;
                break;

            case RatingReadState.ManagerNull:
            case RatingReadState.ResponseNull:
                // Attached and resolved; the game simply hasn't populated MMR yet (between games).
                Health = RatingHealth.Ok;
                _lastSolo = null;
                _lastDuos = null;
                break;

            default: // NotResolvable / StaticsUnresolved
                if (_memory is null || !_memory.TryReadPointer(_memory.ModuleBase, out _))
                {
                    // The module base is unreadable → the process is gone, not a patch mismatch.
                    _diagnostic("target process no longer readable; detaching");
                    Detach(now);
                    Health = RatingHealth.AttachFailed;
                }
                else if (result.State == RatingReadState.StaticsUnresolved)
                {
                    _nextScanAttempt = now + _scanRetryBackoff;
                    _diagnostic(
                        $"class scan did not resolve; retrying in {_scanRetryBackoff.TotalSeconds:F0}s " +
                        "(the class is built lazily — expected before the first Battlegrounds game — " +
                        "otherwise offsets need verification against the live build)");
                    Health = RatingHealth.PatchBroken;
                }
                else
                {
                    _diagnostic("field path did not resolve; offsets likely need verification against the live build");
                    Health = RatingHealth.PatchBroken;
                }

                _lastSolo = null;
                _lastDuos = null;
                break;
        }
    }

    private static RatingHealth MapAttachFault(AttachFault fault) => fault switch
    {
        // Mono gone / replaced by IL2CPP is a structural change, not a transient miss.
        AttachFault.MonoModuleMissing or AttachFault.Il2Cpp => RatingHealth.PatchBroken,
        _ => RatingHealth.AttachFailed,
    };

    private void Detach(DateTimeOffset now)
    {
        (_memory as IDisposable)?.Dispose();
        _memory = null;
        _reader = null;
        _attached = false;
        _lastAttachAttempt = now; // start the re-attach backoff from here
        _nextScanAttempt = DateTimeOffset.MinValue; // a fresh attach gets an immediate first scan
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Detach(_now());
        }
    }
}
