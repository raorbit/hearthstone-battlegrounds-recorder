using BgRecorder.Core.Events;
using BgRecorder.Core.Session;
using BgRecorder.Session;
using Xunit;

namespace BgRecorder.Session.Tests;

public sealed class ManifestStoreTests : IDisposable
{
    private readonly string _dir;

    public ManifestStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "bgrec-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void RoundTrips_AllEventTypes_Polymorphically()
    {
        var events = new GameEvent[]
        {
            new LogSessionChanged(Ev.T0, @"C:\hs\Logs\Hearthstone_2026_07_14_19_59_00"),
            new MatchStarted(Ev.T0.AddSeconds(1)),
            new GameTypeResolved(Ev.T0.AddSeconds(2), BgGameType.Duos),
            new LocalHeroResolved(Ev.T0.AddSeconds(3), "BG31_HERO_001"),
            new TurnStarted(Ev.T0.AddSeconds(4), 3, 2),
            new CombatStarted(Ev.T0.AddSeconds(5), 2),
            new PlacementChanged(Ev.T0.AddSeconds(6), 4),
            new MatchEnded(Ev.T0.AddSeconds(7), 4, PlayState.Conceded, Truncated: false),
        };
        var manifest = new StagingManifest
        {
            SessionId = "abc",
            StartedAt = Ev.T0,
            VideoPath = @"C:\staging\video.mp4",
            AudioPath = @"C:\staging\audio.wav",
            VideoFirstFrameWallClock = Ev.T0.AddSeconds(1.25),
            AudioFirstSampleWallClock = Ev.T0.AddSeconds(1.75),
            Events = events,
            FinalizedCleanly = false,
        };

        ManifestStore.Write(_dir, manifest);
        var read = ManifestStore.TryRead(_dir);

        Assert.NotNull(read);
        Assert.Equal(manifest.SessionId, read!.SessionId);
        Assert.Equal(manifest.StartedAt, read.StartedAt);
        Assert.Equal(manifest.VideoFirstFrameWallClock, read.VideoFirstFrameWallClock);
        Assert.Equal(manifest.AudioFirstSampleWallClock, read.AudioFirstSampleWallClock);
        Assert.False(read.FinalizedCleanly);
        Assert.Equal(events.Length, read.Events.Count);
        for (var i = 0; i < events.Length; i++)
        {
            Assert.Equal(events[i], read.Events[i]); // record equality covers every payload field
        }
    }

    [Fact]
    public void Write_ReplacesAtomically_LatestWins()
    {
        var v1 = new StagingManifest
        {
            SessionId = "s",
            StartedAt = Ev.T0,
            VideoPath = "v",
            AudioPath = "a",
            Events = [new MatchStarted(Ev.T0)],
        };
        ManifestStore.Write(_dir, v1);
        ManifestStore.Write(_dir, v1 with { FinalizedCleanly = true, Events = Ev.FullMatch() });

        var read = ManifestStore.TryRead(_dir);
        Assert.NotNull(read);
        Assert.True(read!.FinalizedCleanly);
        Assert.Equal(Ev.FullMatch().Length, read.Events.Count);
        Assert.False(File.Exists(ManifestStore.PathFor(_dir) + ".tmp")); // no temp litter
    }

    [Fact]
    public void TryRead_CorruptJson_ReturnsNull()
    {
        File.WriteAllText(ManifestStore.PathFor(_dir), "{ definitely not json ]");
        Assert.Null(ManifestStore.TryRead(_dir));
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull()
    {
        Assert.Null(ManifestStore.TryRead(_dir));
    }
}
