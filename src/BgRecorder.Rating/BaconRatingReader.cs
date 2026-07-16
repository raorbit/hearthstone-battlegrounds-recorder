namespace BgRecorder.Rating;

/// <summary>Outcome of one field-path read, distinguishing "not populated yet" from "structure not resolvable".</summary>
internal enum RatingReadState
{
    /// <summary>Full chain resolved and both rating fields read.</summary>
    Ok,

    /// <summary>A class/field/static-data lookup or a metadata read failed — offsets wrong or class not yet built.</summary>
    NotResolvable,

    /// <summary>BaconRatingMgr.s_instance is null — the manager singleton hasn't been created yet.</summary>
    ManagerNull,

    /// <summary>m_lastRatingResponse is null — the game hasn't received a rating response yet.</summary>
    ResponseNull,
}

internal readonly record struct RatingReadResult(RatingReadState State, int Rating, int DuosRating)
{
    public static RatingReadResult NotResolvable => new(RatingReadState.NotResolvable, 0, 0);
    public static RatingReadResult ManagerNull => new(RatingReadState.ManagerNull, 0, 0);
    public static RatingReadResult ResponseNull => new(RatingReadState.ResponseNull, 0, 0);
    public static RatingReadResult Ok(int rating, int duos) => new(RatingReadState.Ok, rating, duos);
}

/// <summary>
/// Walks the specific MMR field path. Class handles and field offsets (metadata, pinned) are resolved once
/// and cached; the object chain (<c>s_instance → m_lastRatingResponse</c>) and the int values are re-read every
/// call, because the GC can move managed objects between polls.
/// </summary>
internal sealed class BaconRatingReader
{
    private const string ManagerNamespace = "";
    private const string ManagerName = "BaconRatingMgr";
    private const string InstanceField = "s_instance";
    private const string ResponseField = "m_lastRatingResponse";
    private const string SoloField = "Rating";
    private const string DuosField = "DuosRating";

    private readonly MonoImageReader _reader;

    // Cached metadata (0 / -1 = unresolved).
    private ulong _managerClass;
    private int _instanceOffset;
    private ulong _staticData;
    private int _responseOffset = -1;
    private int _ratingOffset = -1;
    private int _duosOffset = -1;

    public BaconRatingReader(MonoImageReader reader) =>
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    public RatingReadResult Read(CancellationToken ct = default)
    {
        var mem = _reader.Memory;

        if (!_reader.TryGetRootDomain(out ulong domain) || !EnsureStaticsResolved(domain, ct))
        {
            return RatingReadResult.NotResolvable;
        }

        if (!mem.TryReadPointer(_staticData + (ulong)_instanceOffset, out ulong manager))
        {
            return RatingReadResult.NotResolvable;
        }

        if (manager == 0)
        {
            return RatingReadResult.ManagerNull;
        }

        if (_responseOffset < 0)
        {
            if (!_reader.TryReadObjectClass(manager, out ulong managerClass) ||
                !_reader.TryFindFieldOffset(managerClass, ResponseField, out _responseOffset))
            {
                _responseOffset = -1;
                return RatingReadResult.NotResolvable;
            }
        }

        if (!mem.TryReadPointer(manager + (ulong)_responseOffset, out ulong response))
        {
            return RatingReadResult.NotResolvable;
        }

        if (response == 0)
        {
            return RatingReadResult.ResponseNull;
        }

        if (_ratingOffset < 0 || _duosOffset < 0)
        {
            if (!_reader.TryReadObjectClass(response, out ulong responseClass) ||
                !_reader.TryFindFieldOffset(responseClass, SoloField, out _ratingOffset) ||
                !_reader.TryFindFieldOffset(responseClass, DuosField, out _duosOffset))
            {
                _ratingOffset = -1;
                _duosOffset = -1;
                return RatingReadResult.NotResolvable;
            }
        }

        if (!mem.TryReadInt32(response + (ulong)_ratingOffset, out int rating) ||
            !mem.TryReadInt32(response + (ulong)_duosOffset, out int duos))
        {
            return RatingReadResult.NotResolvable;
        }

        return RatingReadResult.Ok(rating, duos);
    }

    private bool EnsureStaticsResolved(ulong domain, CancellationToken ct)
    {
        if (_managerClass != 0)
        {
            return true;
        }

        if (!_reader.TryFindClass(domain, ManagerNamespace, ManagerName, out ulong klass, ct) ||
            !_reader.TryFindFieldOffset(klass, InstanceField, out int instanceOffset) ||
            !_reader.TryGetStaticData(klass, out ulong staticData))
        {
            return false;
        }

        _managerClass = klass;
        _instanceOffset = instanceOffset;
        _staticData = staticData;
        return true;
    }
}
