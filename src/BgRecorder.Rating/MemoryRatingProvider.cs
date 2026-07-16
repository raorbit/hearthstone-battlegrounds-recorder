using BgRecorder.Core.Events;
using BgRecorder.Core.Rating;

namespace BgRecorder.Rating;

/// <summary>
/// <see cref="IRatingProvider"/> backed by the clean-room external Mono reader. Attaches lazily, polls the
/// live game on a throttle, projects the one read into per-mode snapshots, and — above all — never lets a
/// memory fault escape: any surprise degrades to a health state with a null snapshot, so recording is
/// wholly indifferent to it. Constructed only when <c>AppSettings.EnableMemoryRating</c> is on.
/// </summary>
public sealed class MemoryRatingProvider : IRatingProvider, IDisposable
{
    private readonly IProcessMemoryFactory _factory;
    private readonly MonoOffsets _offsets;
    private readonly Action<string> _diagnostic;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _pollThrottle;
    private readonly TimeSpan _reattachBackoff;
    private readonly object _gate = new();

    private IProcessMemory? _memory;
    private BaconRatingReader? _reader;
    private bool _attached;
    private DateTimeOffset _lastAttachAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;
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
            TimeSpan.FromSeconds(10))
    {
    }

    internal MemoryRatingProvider(
        IProcessMemoryFactory factory,
        MonoOffsets offsets,
        Action<string> diagnostic,
        Func<DateTimeOffset> now,
        TimeSpan pollThrottle,
        TimeSpan reattachBackoff)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _offsets = offsets ?? throw new ArgumentNullException(nameof(offsets));
        _diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _pollThrottle = pollThrottle;
        _reattachBackoff = reattachBackoff;
    }

    public RatingHealth Health { get; private set; } = RatingHealth.AttachFailed;

    public Task<RatingSnapshot?> TryGetAsync(BgGameType mode, CancellationToken ct = default)
    {
        lock (_gate)
        {
            try
            {
                Poll();
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

    private void Poll()
    {
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

        _lastPoll = now;
        ApplyResult(_reader!.Read(), now);
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

            default: // NotResolvable
                if (_memory is null || !_memory.TryReadPointer(_memory.ModuleBase, out _))
                {
                    // The module base is unreadable → the process is gone, not a patch mismatch.
                    _diagnostic("target process no longer readable; detaching");
                    Detach(now);
                    Health = RatingHealth.AttachFailed;
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
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Detach(_now());
        }
    }
}
