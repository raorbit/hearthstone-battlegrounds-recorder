using BgRecorder.Capture.Internal;
using BgRecorder.Core.Capture;
using Xunit;

namespace BgRecorder.Capture.Tests;

public sealed class RecordingSessionTests
{
    private static string NewStagingFile(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "bgrec-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "match.mp4");
    }

    [Fact]
    public void Start_records_to_staging_path()
    {
        var fake = new FakeSrlRecorder();
        var path = NewStagingFile(out var dir);
        try
        {
            var session = new RecordingSessionImpl(fake, path);
            session.Start();

            Assert.Equal(1, fake.RecordCount);
            Assert.Equal(path, fake.LastRecordPath);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Recording_status_maps_to_core_and_seeds_first_frame_fallback()
    {
        var fake = new FakeSrlRecorder();
        var path = NewStagingFile(out var dir);
        try
        {
            var session = new RecordingSessionImpl(fake, path);
            var statuses = new List<RecorderStatus>();
            session.StatusChanged += statuses.Add;
            session.Start();

            Assert.Null(session.FirstFrameWallClock);

            fake.RaiseStatus(SrlStatus.Recording);

            Assert.Contains(RecorderStatus.Recording, statuses);
            // Before any frame, FirstFrameWallClock falls back to the Recording-status stamp.
            Assert.NotNull(session.FirstFrameWallClock);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void First_frame_sets_wall_clock_once_and_disables_preview()
    {
        var fake = new FakeSrlRecorder();
        var path = NewStagingFile(out var dir);
        try
        {
            var session = new RecordingSessionImpl(fake, path);
            session.Start();
            fake.RaiseStatus(SrlStatus.Recording);

            fake.RaiseFrame();
            var first = session.FirstFrameWallClock;
            Assert.NotNull(first);
            Assert.Equal(1, fake.DisablePreviewCount);

            // A second frame neither re-stamps nor re-disables.
            fake.RaiseFrame();
            Assert.Equal(first, session.FirstFrameWallClock);
            Assert.Equal(1, fake.DisablePreviewCount);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task StopAsync_finalizes_and_returns_result_from_disk()
    {
        var fake = new FakeSrlRecorder();
        var path = NewStagingFile(out var dir);
        try
        {
            var payload = new byte[4096];
            File.WriteAllBytes(path, payload);

            var session = new RecordingSessionImpl(fake, path);
            var statuses = new List<RecorderStatus>();
            session.StatusChanged += statuses.Add;
            session.Start();
            fake.RaiseStatus(SrlStatus.Recording);
            fake.RaiseFrame();

            var stopTask = session.StopAsync();
            Assert.Equal(1, fake.StopCount);
            Assert.Contains(RecorderStatus.Finalizing, statuses);

            fake.RaiseCompleted(path);
            var result = await stopTask;

            Assert.Equal(path, result.Path);
            Assert.Equal(payload.Length, result.SizeBytes);
            Assert.True(result.Duration >= TimeSpan.Zero);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task StopAsync_is_idempotent()
    {
        var fake = new FakeSrlRecorder();
        var path = NewStagingFile(out var dir);
        try
        {
            File.WriteAllBytes(path, new byte[16]);
            var session = new RecordingSessionImpl(fake, path);
            session.Start();
            fake.RaiseStatus(SrlStatus.Recording);

            var t1 = session.StopAsync();
            var t2 = session.StopAsync();
            fake.RaiseCompleted(path);

            var r1 = await t1;
            var r2 = await t2;

            Assert.Equal(1, fake.StopCount);          // underlying Stop() driven exactly once
            Assert.Equal(r1, r2);                       // same result to every caller
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Failure_raises_failed_event_and_failed_status()
    {
        var fake = new FakeSrlRecorder();
        var path = NewStagingFile(out var dir);
        try
        {
            var session = new RecordingSessionImpl(fake, path);
            string? failure = null;
            var statuses = new List<RecorderStatus>();
            session.Failed += m => failure = m;
            session.StatusChanged += statuses.Add;
            session.Start();
            fake.RaiseStatus(SrlStatus.Recording);

            fake.RaiseFailed("window vanished");

            Assert.Equal("window vanished", failure);
            Assert.Contains(RecorderStatus.Failed, statuses);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Failure_mid_recording_still_yields_disk_result_on_stop()
    {
        var fake = new FakeSrlRecorder();
        var path = NewStagingFile(out var dir);
        try
        {
            File.WriteAllBytes(path, new byte[2048]); // fragmented MP4 partial the library left behind
            var session = new RecordingSessionImpl(fake, path);
            session.Start();
            fake.RaiseStatus(SrlStatus.Recording);
            fake.RaiseFrame();

            // Spontaneous failure (no Stop yet), then the coordinator stops the session.
            fake.RaiseFailed("device lost");
            var result = await session.StopAsync();

            Assert.Equal(path, result.Path);
            Assert.Equal(2048, result.SizeBytes);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task DisposeAsync_stops_and_disposes_when_still_running()
    {
        var fake = new FakeSrlRecorder();
        var path = NewStagingFile(out var dir);
        try
        {
            File.WriteAllBytes(path, new byte[8]);
            var session = new RecordingSessionImpl(fake, path);
            session.Start();
            fake.RaiseStatus(SrlStatus.Recording);

            // Completion arrives as the library finalizes during dispose-driven stop.
            var disposeTask = session.DisposeAsync();
            fake.RaiseCompleted(path);
            await disposeTask;

            Assert.True(fake.StopCount >= 1);
            Assert.True(fake.Disposed);
        }
        finally { Directory.Delete(dir, true); }
    }
}
