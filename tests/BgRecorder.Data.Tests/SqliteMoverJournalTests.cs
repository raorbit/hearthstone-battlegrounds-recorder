using BgRecorder.Core.Storage;
using BgRecorder.Data;
using Xunit;

namespace BgRecorder.Data.Tests;

public sealed class SqliteMoverJournalTests
{
    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "bgmj-" + Guid.NewGuid().ToString("N") + ".db");

    private static void Cleanup(string dbPath)
    {
        foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Append_list_update_and_remove_round_trip()
    {
        var db = NewDbPath();
        try
        {
            var journal = new SqliteMoverJournal(db);
            await journal.InitializeAsync();

            var id = await journal.AppendAsync(new MoverJournalEntry
            {
                MatchId = 5,
                SourcePath = @"C:\rec\a.mp4",
                DestinationPath = @"D:\arc\a.mp4",
                SourceHash = "ABCD",
                State = MoverJournalState.Copying,
                CreatedAt = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
            });

            var listed = Assert.Single(await journal.ListAsync());
            Assert.Equal(id, listed.Id);
            Assert.Equal(5, listed.MatchId);
            Assert.Equal(@"C:\rec\a.mp4", listed.SourcePath);
            Assert.Equal(@"D:\arc\a.mp4", listed.DestinationPath);
            Assert.Equal("ABCD", listed.SourceHash);
            Assert.Equal(MoverJournalState.Copying, listed.State);

            await journal.UpdateStateAsync(id, MoverJournalState.Flipping);
            Assert.Equal(MoverJournalState.Flipping, (await journal.ListAsync()).Single().State);

            await journal.RemoveAsync(id);
            Assert.Empty(await journal.ListAsync());
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task Entries_are_listed_oldest_first()
    {
        var db = NewDbPath();
        try
        {
            var journal = new SqliteMoverJournal(db);
            await journal.InitializeAsync();
            var entry = new MoverJournalEntry
            {
                MatchId = 1,
                SourcePath = "s",
                DestinationPath = "d",
                State = MoverJournalState.Copying,
                CreatedAt = DateTimeOffset.UnixEpoch,
            };

            var id1 = await journal.AppendAsync(entry);
            var id2 = await journal.AppendAsync(entry with { MatchId = 2 });

            var list = await journal.ListAsync();
            Assert.Equal([id1, id2], list.Select(x => x.Id));
            Assert.Equal([1L, 2L], list.Select(x => x.MatchId));
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task Initialize_is_idempotent()
    {
        var db = NewDbPath();
        try
        {
            var journal = new SqliteMoverJournal(db);
            await journal.InitializeAsync();
            await journal.InitializeAsync(); // second call must not throw or wipe data
            Assert.Empty(await journal.ListAsync());
        }
        finally { Cleanup(db); }
    }
}
