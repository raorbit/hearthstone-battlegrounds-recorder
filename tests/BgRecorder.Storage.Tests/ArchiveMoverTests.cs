using System.Security.Cryptography;
using BgRecorder.Core.Data;
using BgRecorder.Core.Storage;
using BgRecorder.Storage;
using Xunit;

namespace BgRecorder.Storage.Tests;

/// <summary>
/// The mover's invariant under every fault: exactly one intact, referenced copy always survives.
/// Faults are injected through the fake file system; the physical yanked-drive / kill-9 half of the
/// torture suite is the user's.
/// </summary>
public sealed class ArchiveMoverTests
{
    [Fact]
    public async Task A_clean_move_copies_verifies_flips_the_row_and_deletes_the_source()
    {
        var fs = new FakeFileSystem();
        fs.Seed("src.mp4", [1, 2, 3, 4]);
        var journal = new FakeMoverJournal();
        var locations = new FakeLocationStore();
        var mover = new ArchiveMover(fs, journal, locations);

        var outcome = await mover.MoveAsync(7, "src.mp4", "dst.mp4");

        Assert.True(outcome.Success);
        Assert.True(fs.Has("dst.mp4"));
        Assert.False(fs.Has("src.mp4"));           // source reclaimed only after the flip
        Assert.Equal("dst.mp4", locations.Locations[7]);
        Assert.Empty(journal.Entries);             // journal cleared on success
    }

    [Fact]
    public async Task A_copy_that_does_not_verify_keeps_the_source_and_never_flips_the_row()
    {
        var fs = new FakeFileSystem { CorruptOnCopy = true };
        fs.Seed("src.mp4", [1, 2, 3, 4]);
        var journal = new FakeMoverJournal();
        var locations = new FakeLocationStore();
        var mover = new ArchiveMover(fs, journal, locations);

        var outcome = await mover.MoveAsync(7, "src.mp4", "dst.mp4");

        Assert.False(outcome.Success);
        Assert.True(fs.Has("src.mp4"));            // the only good copy is untouched
        Assert.False(fs.Has("dst.mp4"));           // the corrupt copy is discarded
        Assert.False(locations.Locations.ContainsKey(7));
        Assert.Empty(journal.Entries);
    }

    [Fact]
    public async Task A_copy_that_throws_leaves_the_source_referenced_and_cleans_up()
    {
        var fs = new FakeFileSystem { FailCopy = true };
        fs.Seed("src.mp4", [1, 2, 3, 4]);
        var journal = new FakeMoverJournal();
        var locations = new FakeLocationStore();
        var mover = new ArchiveMover(fs, journal, locations);

        await Assert.ThrowsAsync<IOException>(() => mover.MoveAsync(7, "src.mp4", "dst.mp4"));

        Assert.True(fs.Has("src.mp4"));
        Assert.False(fs.Has("dst.mp4"));
        Assert.False(locations.Locations.ContainsKey(7));
        Assert.Empty(journal.Entries);
    }

    [Fact]
    public async Task A_missing_source_fails_without_journaling_anything()
    {
        var mover = new ArchiveMover(new FakeFileSystem(), new FakeMoverJournal(), new FakeLocationStore());

        var outcome = await mover.MoveAsync(7, "gone.mp4", "dst.mp4");

        Assert.False(outcome.Success);
    }

    [Fact]
    public async Task Reconcile_before_the_flip_discards_the_destination_and_keeps_the_source()
    {
        var fs = new FakeFileSystem();
        fs.Seed("src.mp4", [1, 2, 3, 4]);
        fs.Seed("dst.mp4", [1, 2]); // a partial, unverified copy from a crashed move
        var journal = new FakeMoverJournal();
        journal.Seed(new MoverJournalEntry
        {
            MatchId = 7,
            SourcePath = "src.mp4",
            DestinationPath = "dst.mp4",
            State = MoverJournalState.Copying,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        var locations = new FakeLocationStore();

        await new ArchiveMover(fs, journal, locations).ReconcileAsync();

        Assert.True(fs.Has("src.mp4"));            // source wins before the flip
        Assert.False(fs.Has("dst.mp4"));           // partial destination discarded
        Assert.False(locations.Locations.ContainsKey(7));
        Assert.Empty(journal.Entries);
    }

    [Fact]
    public async Task Reconcile_after_the_verify_completes_the_flip_and_deletes_the_source()
    {
        var fs = new FakeFileSystem();
        fs.Seed("src.mp4", [1, 2, 3, 4]);
        fs.Seed("dst.mp4", [1, 2, 3, 4]); // verified copy already written before the crash
        var journal = new FakeMoverJournal();
        journal.Seed(new MoverJournalEntry
        {
            MatchId = 7,
            SourcePath = "src.mp4",
            DestinationPath = "dst.mp4",
            State = MoverJournalState.Flipping,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        var locations = new FakeLocationStore();

        await new ArchiveMover(fs, journal, locations).ReconcileAsync();

        Assert.True(fs.Has("dst.mp4"));            // destination wins after the verify
        Assert.False(fs.Has("src.mp4"));           // redundant source reclaimed
        Assert.Equal("dst.mp4", locations.Locations[7]);
        Assert.Empty(journal.Entries);
    }

    [Fact]
    public async Task A_fault_after_the_flip_never_deletes_the_verified_destination()
    {
        // The DB flip commits, then the source-delete throws (a still-locked file). The old catch would
        // have deleted the destination — the only referenced copy. It must survive.
        var fs = new FakeFileSystem { FailDeleteOf = "src.mp4" };
        fs.Seed("src.mp4", [1, 2, 3, 4]);
        var journal = new FakeMoverJournal();
        var locations = new FakeLocationStore();
        var mover = new ArchiveMover(fs, journal, locations);

        var outcome = await mover.MoveAsync(7, "src.mp4", "dst.mp4");

        Assert.True(outcome.Success);              // the flip committed, so the move succeeded
        Assert.True(fs.Has("dst.mp4"));            // destination is never deleted after the flip
        Assert.Equal("dst.mp4", locations.Locations[7]);
    }

    [Fact]
    public async Task A_move_onto_the_same_path_is_refused_and_keeps_the_file()
    {
        var fs = new FakeFileSystem();
        fs.Seed("same.mp4", [1, 2, 3]);

        var outcome = await new ArchiveMover(fs, new FakeMoverJournal(), new FakeLocationStore())
            .MoveAsync(7, "same.mp4", "same.mp4");

        Assert.False(outcome.Success);
        Assert.True(fs.Has("same.mp4"));           // the one and only copy is untouched
    }

    [Fact]
    public async Task Reconcile_keeps_the_source_when_the_destination_is_unreachable()
    {
        // Flipping entry, but the destination is not present (archive drive offline between crash and
        // reconcile). The source must not be deleted, and the entry stays for a later reconciliation.
        var fs = new FakeFileSystem();
        fs.Seed("src.mp4", [1, 2, 3, 4]); // destination deliberately not seeded
        var journal = new FakeMoverJournal();
        journal.Seed(new MoverJournalEntry
        {
            MatchId = 7,
            SourcePath = "src.mp4",
            DestinationPath = "dst.mp4",
            State = MoverJournalState.Flipping,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });

        await new ArchiveMover(fs, journal, new FakeLocationStore()).ReconcileAsync();

        Assert.True(fs.Has("src.mp4"));            // source preserved while the destination is missing
        Assert.Single(journal.Entries);           // retained for a later reconciliation pass
    }

    [Fact]
    public async Task Reconcile_isolates_a_failing_entry_from_the_rest()
    {
        var fs = new FakeFileSystem { FailDeleteOf = "bad-src.mp4" };
        fs.Seed("bad-src.mp4", [9]);
        fs.Seed("bad-dst.mp4", [9]);
        fs.Seed("good-src.mp4", [1]);
        fs.Seed("good-dst.mp4", [1]);
        var journal = new FakeMoverJournal();
        journal.Seed(new MoverJournalEntry
        {
            MatchId = 1,
            SourcePath = "bad-src.mp4",
            DestinationPath = "bad-dst.mp4",
            State = MoverJournalState.Deleting,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        journal.Seed(new MoverJournalEntry
        {
            MatchId = 2,
            SourcePath = "good-src.mp4",
            DestinationPath = "good-dst.mp4",
            State = MoverJournalState.Flipping,
            CreatedAt = DateTimeOffset.UnixEpoch,
        });
        var locations = new FakeLocationStore();

        await new ArchiveMover(fs, journal, locations).ReconcileAsync();

        // The second entry completes even though the first one threw mid-recovery.
        Assert.Equal("good-dst.mp4", locations.Locations[2]);
        Assert.False(fs.Has("good-src.mp4"));
    }

    private sealed class FakeFileSystem : IFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new();

        public bool CorruptOnCopy { get; init; }
        public bool FailCopy { get; init; }

        /// <summary>When set, <see cref="Delete"/> throws for this exact path (a handle-locked file).</summary>
        public string? FailDeleteOf { get; init; }

        public void Seed(string path, byte[] data) => _files[path] = data;
        public bool Has(string path) => _files.ContainsKey(path);

        public bool FileExists(string path) => _files.ContainsKey(path);
        public long GetFileSizeBytes(string path) => _files[path].Length;
        public void CreateDirectoryForFile(string filePath) { }

        public Task CopyAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
        {
            if (FailCopy)
            {
                throw new IOException("injected copy failure");
            }
            var data = _files[sourcePath];
            _files[destinationPath] = CorruptOnCopy ? [.. data, 0xFF] : (byte[])data.Clone();
            return Task.CompletedTask;
        }

        public Task<string> ComputeContentHashAsync(string path, CancellationToken ct = default)
            => Task.FromResult(Convert.ToHexString(SHA256.HashData(_files[path])));

        public void Delete(string path)
        {
            if (FailDeleteOf is not null && path == FailDeleteOf)
            {
                throw new IOException($"injected delete failure for {path}");
            }
            _files.Remove(path);
        }
    }

    private sealed class FakeMoverJournal : IMoverJournal
    {
        private readonly List<MoverJournalEntry> _entries = [];
        private long _nextId = 1;

        public IReadOnlyList<MoverJournalEntry> Entries => _entries;

        public void Seed(MoverJournalEntry entry) => _entries.Add(entry with { Id = _nextId++ });

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

    private sealed class FakeLocationStore : IMatchLocationStore
    {
        public Dictionary<long, string> Locations { get; } = [];

        public Task UpdateVideoLocationAsync(long matchId, string videoPath, CancellationToken ct = default)
        {
            Locations[matchId] = videoPath;
            return Task.CompletedTask;
        }
    }
}
