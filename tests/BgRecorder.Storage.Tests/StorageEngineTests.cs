using System.Security.Cryptography;
using BgRecorder.Core.Data;
using BgRecorder.Core.Events;
using BgRecorder.Core.Storage;
using BgRecorder.Storage;
using Xunit;

namespace BgRecorder.Storage.Tests;

/// <summary>
/// End-to-end wiring: the engine projects the library onto configured volumes, runs the real policy,
/// and drives the real mover / deletes. Uses in-memory fakes for the file system, journal, repo, and
/// free-space probe — no real drives, so paths like C:\lib are just identifiers.
/// </summary>
public sealed class StorageEngineTests
{
    private const long GB = 1024L * 1024 * 1024;
    private const string RecordingDir = @"C:\lib";
    private const string ArchiveDir = @"D:\arc";

    private static MatchRecord Match(long id, string videoPath, long sizeGb, int ageDays, bool starred = false) => new()
    {
        Id = id,
        StartedAt = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero).AddDays(-ageDays),
        GameType = BgGameType.Solo,
        TavernTurns = 10,
        VideoStatus = VideoStatus.Complete,
        VideoPath = videoPath,
        VideoSizeBytes = sizeGb * GB,
        Starred = starred,
    };

    [Fact]
    public async Task Over_cap_with_an_archive_moves_the_oldest_recording_to_it()
    {
        var fs = new FakeFileSystem();
        fs.Seed(@"C:\lib\m1.mp4", [1]);
        fs.Seed(@"C:\lib\m2.mp4", [2]);
        fs.Seed(@"C:\lib\m3.mp4", [3]);
        var store = new FakeMatchStore(
            Match(1, @"C:\lib\m1.mp4", 5, ageDays: 3),
            Match(2, @"C:\lib\m2.mp4", 5, ageDays: 2),
            Match(3, @"C:\lib\m3.mp4", 5, ageDays: 1));
        var engine = BuildEngine(fs, store, new StorageOptions
        {
            RecordingCapBytes = 10 * GB,
            RecordingReserveBytes = 1 * GB,
            HotSetSize = 1,
            ArchiveVolumes = [new ArchiveVolumeOptions { Directory = ArchiveDir, CapBytes = 100 * GB, ReserveBytes = 1 * GB }],
        });

        var report = await engine.EnforceAsync();

        Assert.Equal(1, report.MovesExecuted);
        Assert.Equal(0, report.DeletesExecuted);
        Assert.True(fs.Has(@"D:\arc\m1.mp4"));      // oldest moved to the archive
        Assert.False(fs.Has(@"C:\lib\m1.mp4"));     // source reclaimed
        Assert.Equal(@"D:\arc\m1.mp4", store.PathOf(1));
    }

    [Fact]
    public async Task Over_cap_with_no_archive_deletes_the_oldest_unstarred_recording()
    {
        var fs = new FakeFileSystem();
        fs.Seed(@"C:\lib\m1.mp4", [1]);
        fs.Seed(@"C:\lib\m2.mp4", [2]);
        fs.Seed(@"C:\lib\m3.mp4", [3]);
        var store = new FakeMatchStore(
            Match(1, @"C:\lib\m1.mp4", 5, ageDays: 3),   // oldest, unstarred → evicted
            Match(2, @"C:\lib\m2.mp4", 5, ageDays: 2),
            Match(3, @"C:\lib\m3.mp4", 5, ageDays: 1));
        var engine = BuildEngine(fs, store, new StorageOptions
        {
            RecordingCapBytes = 10 * GB,
            RecordingReserveBytes = 1 * GB,
            HotSetSize = 1,
        });

        var report = await engine.EnforceAsync();

        Assert.Equal(0, report.MovesExecuted);
        Assert.Equal(1, report.DeletesExecuted);
        Assert.False(fs.Has(@"C:\lib\m1.mp4"));     // file removed
        Assert.Null(store.TryGet(1));               // row removed
        Assert.NotNull(store.TryGet(3));            // hot set untouched
    }

    [Fact]
    public async Task A_library_within_its_cap_changes_nothing()
    {
        var fs = new FakeFileSystem();
        fs.Seed(@"C:\lib\m1.mp4", [1]);
        var store = new FakeMatchStore(Match(1, @"C:\lib\m1.mp4", 5, ageDays: 1));
        var engine = BuildEngine(fs, store, new StorageOptions { RecordingCapBytes = 100 * GB, RecordingReserveBytes = 1 * GB });

        var report = await engine.EnforceAsync();

        Assert.Equal(0, report.MovesExecuted);
        Assert.Equal(0, report.DeletesExecuted);
        Assert.True(fs.Has(@"C:\lib\m1.mp4"));
    }

    private static StorageEngine BuildEngine(FakeFileSystem fs, FakeMatchStore store, StorageOptions options)
    {
        var mover = new ArchiveMover(fs, new FakeMoverJournal(), store);
        return new StorageEngine(store, new RetentionPolicy(), mover, new FakeFreeSpaceProbe(200 * GB), fs, RecordingDir, options);
    }

    private sealed class FakeFreeSpaceProbe(long free) : IFreeSpaceProbe
    {
        public long GetAvailableFreeBytes(string path) => free;
    }

    private sealed class FakeMatchStore : IMatchStore
    {
        private readonly Dictionary<long, MatchRecord> _matches;

        public FakeMatchStore(params MatchRecord[] matches) => _matches = matches.ToDictionary(m => m.Id);

        public MatchRecord? TryGet(long id) => _matches.GetValueOrDefault(id);
        public string? PathOf(long id) => _matches.GetValueOrDefault(id)?.VideoPath;

        public Task<IReadOnlyList<MatchRecord>> ListMatchesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MatchRecord>>(_matches.Values.ToList());

        public Task DeleteMatchAsync(long matchId, CancellationToken ct = default)
        {
            _matches.Remove(matchId);
            return Task.CompletedTask;
        }

        public Task UpdateVideoLocationAsync(long matchId, string videoPath, CancellationToken ct = default)
        {
            if (_matches.TryGetValue(matchId, out var record))
            {
                _matches[matchId] = record with { VideoPath = videoPath };
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public void Seed(string path, byte[] data) => _files[path] = data;
        public bool Has(string path) => _files.ContainsKey(path);

        public bool FileExists(string path) => _files.ContainsKey(path);
        public long GetFileSizeBytes(string path) => _files[path].Length;
        public void CreateDirectoryForFile(string filePath) { }

        public Task CopyAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
        {
            _files[destinationPath] = (byte[])_files[sourcePath].Clone();
            return Task.CompletedTask;
        }

        public Task<string> ComputeContentHashAsync(string path, CancellationToken ct = default)
            => Task.FromResult(Convert.ToHexString(SHA256.HashData(_files[path])));

        public void Delete(string path) => _files.Remove(path);
    }

    private sealed class FakeMoverJournal : IMoverJournal
    {
        private readonly List<MoverJournalEntry> _entries = [];
        private long _nextId = 1;

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<long> AppendAsync(MoverJournalEntry entry, CancellationToken ct = default)
        {
            var id = _nextId++;
            _entries.Add(entry with { Id = id });
            return Task.FromResult(id);
        }

        public Task UpdateStateAsync(long id, MoverJournalState state, CancellationToken ct = default)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Id == id)
                {
                    _entries[i] = _entries[i] with { State = state };
                }
            }
            return Task.CompletedTask;
        }

        public Task RemoveAsync(long id, CancellationToken ct = default)
        {
            _entries.RemoveAll(e => e.Id == id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MoverJournalEntry>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MoverJournalEntry>>(_entries.OrderBy(e => e.Id).ToList());
    }
}
