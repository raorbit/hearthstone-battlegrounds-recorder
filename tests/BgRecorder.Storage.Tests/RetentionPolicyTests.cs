using BgRecorder.Core.Storage;
using BgRecorder.Storage;
using Xunit;

namespace BgRecorder.Storage.Tests;

/// <summary>
/// The pure-logic half of the M5 torture suite: every retention rule from the plan, exercised without
/// touching a filesystem. The VHDX/physical-drive half (yanked drive, kill-9 mid-move) is the user's.
/// </summary>
public sealed class RetentionPolicyTests
{
    private const long GB = 1024L * 1024 * 1024;
    private static readonly DateTimeOffset Base = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

    private static readonly RetentionPolicy Policy = new();

    private static ManagedVolume Rec(long capGb, long reserveGb, long freeGb) => new()
    {
        Id = "REC",
        Role = VolumeRole.Recording,
        CapBytes = capGb * GB,
        ReserveBytes = reserveGb * GB,
        FreeBytes = freeGb * GB,
    };

    private static ManagedVolume Arc(
        string id, long capGb, long reserveGb, long freeGb, int priority = 0, bool online = true) => new()
        {
            Id = id,
            Role = VolumeRole.Archive,
            CapBytes = capGb * GB,
            ReserveBytes = reserveGb * GB,
            FreeBytes = freeGb * GB,
            Priority = priority,
            IsOnline = online,
        };

    // ageDays: larger = older. So the oldest-finished match evicts first.
    private static StoredMatch M(long id, string volumeId, long sizeGb, int ageDays, bool starred = false) => new()
    {
        MatchId = id,
        VolumeId = volumeId,
        SizeBytes = sizeGb * GB,
        Starred = starred,
        FinishedAt = Base.AddDays(-ageDays),
    };

    private static StorageState State(
        IReadOnlyList<ManagedVolume> volumes,
        IReadOnlyList<StoredMatch> matches,
        int hotSet,
        long? totalCapGb = null) => new()
        {
            Volumes = volumes,
            Matches = matches,
            HotSetSize = hotSet,
            TotalCapBytes = totalCapGb is { } t ? t * GB : null,
        };

    [Fact]
    public void Within_budget_produces_an_empty_plan()
    {
        var plan = Policy.Plan(State(
            [Rec(capGb: 100, reserveGb: 10, freeGb: 50)],
            [M(1, "REC", sizeGb: 5, ageDays: 0)],
            hotSet: 5));

        Assert.Empty(plan.Moves);
        Assert.Empty(plan.Deletes);
        Assert.False(plan.RecordingBelowFloor);
    }

    [Fact]
    public void Over_cap_moves_oldest_first_to_the_archive_including_a_starred_match()
    {
        var plan = Policy.Plan(State(
            [Rec(capGb: 10, reserveGb: 5, freeGb: 20), Arc("ARC", capGb: 100, reserveGb: 5, freeGb: 100)],
            [
                M(1, "REC", sizeGb: 5, ageDays: 3, starred: true), // oldest, starred — still movable
                M(2, "REC", sizeGb: 5, ageDays: 2),
                M(3, "REC", sizeGb: 5, ageDays: 1),                // newest → pinned by hot set
            ],
            hotSet: 1));

        var move = Assert.Single(plan.Moves);
        Assert.Equal(1, move.MatchId); // oldest evicted first, even though starred (lossless)
        Assert.Equal("ARC", move.ToVolumeId);
        Assert.Empty(plan.Deletes);
        Assert.False(plan.RecordingBelowFloor);
    }

    [Fact]
    public void Over_cap_with_no_archive_deletes_the_oldest_unstarred()
    {
        var plan = Policy.Plan(State(
            [Rec(capGb: 10, reserveGb: 5, freeGb: 20)],
            [M(1, "REC", 5, ageDays: 3), M(2, "REC", 5, ageDays: 2), M(3, "REC", 5, ageDays: 1)],
            hotSet: 1));

        var delete = Assert.Single(plan.Deletes);
        Assert.Equal(1, delete.MatchId);
        Assert.Equal(RetentionDeleteReason.RecordingVolumeOverBudget, delete.Reason);
        Assert.Empty(plan.Moves);
        Assert.False(plan.RecordingBelowFloor);
    }

    [Fact]
    public void A_starred_oldest_match_is_skipped_so_a_newer_unstarred_one_is_deleted_instead()
    {
        var plan = Policy.Plan(State(
            [Rec(capGb: 10, reserveGb: 5, freeGb: 20)],
            [
                M(1, "REC", 5, ageDays: 3, starred: true), // oldest but starred — inviolable
                M(2, "REC", 5, ageDays: 2),                // deleted instead
                M(3, "REC", 5, ageDays: 1),                // hot set
            ],
            hotSet: 1));

        var delete = Assert.Single(plan.Deletes);
        Assert.Equal(2, delete.MatchId);
        Assert.DoesNotContain(plan.Deletes, d => d.MatchId == 1);
    }

    [Fact]
    public void The_hot_set_is_never_evicted_even_when_over_cap()
    {
        // Three 5 GB matches, cap 5 GB, but all three are the newest K — pinned. Disk has room, so
        // being over the soft cap does not block arming.
        var plan = Policy.Plan(State(
            [Rec(capGb: 5, reserveGb: 2, freeGb: 20)],
            [M(1, "REC", 5, ageDays: 3), M(2, "REC", 5, ageDays: 2), M(3, "REC", 5, ageDays: 1)],
            hotSet: 3));

        Assert.Empty(plan.Moves);
        Assert.Empty(plan.Deletes);
        Assert.False(plan.RecordingBelowFloor);
    }

    [Fact]
    public void A_move_that_would_break_the_archive_reserve_floor_is_refused_and_falls_back_to_delete()
    {
        // Archive has 12 GB free with a 5 GB reserve; a 10 GB move would leave 2 GB < reserve, so it
        // must not move there. The match is unstarred, so it is deleted off the recording drive.
        var plan = Policy.Plan(State(
            [Rec(capGb: 5, reserveGb: 2, freeGb: 20), Arc("ARC", capGb: 100, reserveGb: 5, freeGb: 12)],
            [M(1, "REC", 10, ageDays: 2), M(2, "REC", 3, ageDays: 1)],
            hotSet: 1));

        Assert.Empty(plan.Moves);
        var delete = Assert.Single(plan.Deletes);
        Assert.Equal(1, delete.MatchId);
    }

    [Fact]
    public void Below_the_reserve_floor_with_only_starred_left_blocks_arming_and_deletes_nothing()
    {
        // Free (5 GB) is under the reserve floor (10 GB); the only evictable match is starred and there
        // is no archive — so nothing is deleted and the host must refuse to arm.
        var plan = Policy.Plan(State(
            [Rec(capGb: 100, reserveGb: 10, freeGb: 5)],
            [
                M(1, "REC", 5, ageDays: 3, starred: true), // evictable candidate, but starred
                M(2, "REC", 5, ageDays: 1, starred: true), // hot set
            ],
            hotSet: 1));

        Assert.Empty(plan.Moves);
        Assert.Empty(plan.Deletes);
        Assert.True(plan.RecordingBelowFloor);
    }

    [Fact]
    public void An_offline_archive_is_not_used_as_a_move_target()
    {
        var plan = Policy.Plan(State(
            [
                Rec(capGb: 10, reserveGb: 5, freeGb: 20),
                Arc("ARC", capGb: 100, reserveGb: 5, freeGb: 100, online: false),
            ],
            [M(1, "REC", 5, ageDays: 3), M(2, "REC", 5, ageDays: 2), M(3, "REC", 5, ageDays: 1)],
            hotSet: 1));

        Assert.Empty(plan.Moves);
        var delete = Assert.Single(plan.Deletes);
        Assert.Equal(1, delete.MatchId);
    }

    [Fact]
    public void The_total_library_cap_deletes_the_oldest_unstarred_across_all_volumes()
    {
        // Every per-volume cap has room, but the whole library (15 GB) exceeds the total cap (12 GB).
        var plan = Policy.Plan(State(
            [Rec(capGb: 100, reserveGb: 5, freeGb: 100), Arc("ARC", capGb: 100, reserveGb: 5, freeGb: 100)],
            [
                M(1, "REC", 5, ageDays: 3), // oldest unstarred anywhere
                M(2, "ARC", 5, ageDays: 2),
                M(3, "REC", 5, ageDays: 1), // hot set
            ],
            hotSet: 1,
            totalCapGb: 12));

        var delete = Assert.Single(plan.Deletes);
        Assert.Equal(1, delete.MatchId);
        Assert.Equal(RetentionDeleteReason.TotalLibraryOverCap, delete.Reason);
        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void The_highest_priority_archive_with_headroom_receives_the_move()
    {
        var plan = Policy.Plan(State(
            [
                Rec(capGb: 4, reserveGb: 2, freeGb: 20),
                Arc("ARC2", capGb: 100, reserveGb: 5, freeGb: 100, priority: 1),
                Arc("ARC1", capGb: 100, reserveGb: 5, freeGb: 100, priority: 0),
            ],
            [M(1, "REC", 5, ageDays: 2), M(2, "REC", 5, ageDays: 1)],
            hotSet: 1));

        var move = Assert.Single(plan.Moves);
        Assert.Equal(1, move.MatchId);
        Assert.Equal("ARC1", move.ToVolumeId); // priority 0 beats priority 1
    }

    [Fact]
    public void A_full_high_priority_archive_overflows_to_the_next_one()
    {
        // ARC1 is at its cap; the move must overflow to ARC2.
        var plan = Policy.Plan(State(
            [
                Rec(capGb: 4, reserveGb: 2, freeGb: 20),
                Arc("ARC1", capGb: 3, reserveGb: 1, freeGb: 100, priority: 0),
                Arc("ARC2", capGb: 100, reserveGb: 5, freeGb: 100, priority: 1),
            ],
            [M(1, "REC", 5, ageDays: 2), M(2, "REC", 5, ageDays: 1)],
            hotSet: 1));

        var move = Assert.Single(plan.Moves);
        Assert.Equal("ARC2", move.ToVolumeId); // ARC1 lacks cap headroom for a 5 GB match
    }

    [Fact]
    public void The_total_cap_never_deletes_a_match_on_an_offline_archive()
    {
        // Rule 7: an unplugged archive keeps everything. The total-cap pass must not target its rows.
        var plan = Policy.Plan(State(
            [
                Rec(capGb: 100, reserveGb: 5, freeGb: 100),
                Arc("ARC", capGb: 100, reserveGb: 5, freeGb: 100, online: false),
            ],
            [M(1, "ARC", 5, ageDays: 3), M(2, "ARC", 5, ageDays: 2), M(3, "REC", 5, ageDays: 1)],
            hotSet: 1,
            totalCapGb: 8));

        Assert.Empty(plan.Deletes);
        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void A_match_on_an_unmanaged_volume_is_ignored_rather_than_crashing_the_total_cap_pass()
    {
        // "GHOST" is a dropped drive whose rows still exist; it must not be a deletion candidate and
        // must not throw when its bytes cannot be reconciled against a managed volume.
        var plan = Policy.Plan(State(
            [Rec(capGb: 100, reserveGb: 5, freeGb: 100)],
            [M(1, "GHOST", 5, ageDays: 3), M(2, "REC", 5, ageDays: 1)],
            hotSet: 1,
            totalCapGb: 4));

        Assert.DoesNotContain(plan.Deletes, d => d.MatchId == 1);
    }

    [Fact]
    public void More_than_one_recording_volume_plans_nothing_rather_than_crashing()
    {
        var plan = Policy.Plan(State(
            [Rec(1, 0, 1) with { Id = "REC1" }, Rec(1, 0, 1) with { Id = "REC2" }],
            [M(1, "REC1", 5, ageDays: 1)],
            hotSet: 0));

        Assert.Empty(plan.Moves);
        Assert.Empty(plan.Deletes);
        Assert.False(plan.RecordingBelowFloor);
    }

    [Fact]
    public void Duplicate_volume_ids_plan_nothing_rather_than_crashing()
    {
        var plan = Policy.Plan(State(
            [Rec(1, 0, 1), Arc("REC", capGb: 1, reserveGb: 0, freeGb: 1)], // both share id "REC"
            [M(1, "REC", 5, ageDays: 1)],
            hotSet: 0));

        Assert.Empty(plan.Moves);
        Assert.Empty(plan.Deletes);
    }
}
