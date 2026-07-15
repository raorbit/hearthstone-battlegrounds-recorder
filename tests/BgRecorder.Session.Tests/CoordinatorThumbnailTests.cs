using BgRecorder.Core.Events;
using Xunit;

namespace BgRecorder.Session.Tests;

/// <summary>
/// The thumbnail step at finalize is strictly best-effort: it stamps a path when extraction succeeds,
/// records the row with no thumbnail when it fails, and — most importantly — a thumbnail fault must
/// never abort finalize (the match row and VOD are what matter).
/// </summary>
public sealed class CoordinatorThumbnailTests
{
    [Fact]
    public async Task Successful_extraction_stamps_the_thumbnail_path_on_the_inserted_row()
    {
        await using var h = new CoordinatorHarness();
        h.Thumbnailer.Succeed = true;
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.WaitUntilAsync(() => h.Repository.Inserted.Count == 1, what: "row inserted");

        var inserted = Assert.Single(h.Repository.Inserted);
        Assert.False(string.IsNullOrEmpty(inserted.Match.ThumbnailPath));
        Assert.Single(h.Thumbnailer.Calls); // generated once, from the finalized library MP4
    }

    [Fact]
    public async Task A_failed_extraction_inserts_the_row_with_no_thumbnail()
    {
        await using var h = new CoordinatorHarness();
        h.Thumbnailer.Succeed = false; // reports failure, no exception
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.WaitUntilAsync(() => h.Repository.Inserted.Count == 1, what: "row inserted");

        Assert.Null(Assert.Single(h.Repository.Inserted).Match.ThumbnailPath);
    }

    [Fact]
    public async Task A_throwing_extractor_never_aborts_finalize()
    {
        await using var h = new CoordinatorHarness();
        h.Thumbnailer.Throw = true; // extraction throws mid-finalize
        await h.StartAsync();
        await h.StartMatchAsync();

        h.Source.Raise(new MatchEnded(Ev.T0.AddMinutes(12), 3, PlayState.Lost, Truncated: false));
        await h.WaitUntilAsync(() => h.Repository.Inserted.Count == 1, what: "row still inserted despite the thumbnail fault");

        // Finalize completed normally: the row committed with a null thumbnail and staging was reclaimed.
        Assert.Null(Assert.Single(h.Repository.Inserted).Match.ThumbnailPath);
        Assert.Empty(Directory.GetDirectories(h.StagingDir));
    }
}
