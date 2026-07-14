using BgRecorder.Core.Events;

namespace BgRecorder.Core.Rating;

/// <summary>
/// Optional, degradable rating source. v1 ships NullRatingProvider (M1 licensing decision:
/// HearthMirror is NO-GO; the clean-room memory reader is post-v1). Recording never depends on this.
/// </summary>
public interface IRatingProvider
{
    RatingHealth Health { get; }

    /// <summary>Null when unavailable — callers render "—" and carry on.</summary>
    Task<RatingSnapshot?> TryGetAsync(BgGameType mode, CancellationToken ct = default);
}

public enum RatingHealth
{
    Disabled = 0,
    Ok = 1,
    AttachFailed = 2,
    PatchBroken = 3,
}

public sealed record RatingSnapshot(BgGameType Mode, int Rating, DateTimeOffset SampledAt);

public sealed class NullRatingProvider : IRatingProvider
{
    public RatingHealth Health => RatingHealth.Disabled;

    public Task<RatingSnapshot?> TryGetAsync(BgGameType mode, CancellationToken ct = default)
        => Task.FromResult<RatingSnapshot?>(null);
}
