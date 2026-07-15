using BgRecorder.Core;
using Xunit;

namespace BgRecorder.Data.Tests;

public sealed class JsonSettingsServiceTests
{
    private static string NewPath() =>
        Path.Combine(Path.GetTempPath(), $"bgrec-settings-{Guid.NewGuid():N}.json");

    private static void Cleanup(string path)
    {
        foreach (var p in new[] { path, path + ".tmp", path + ".corrupt" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort in tests */ }
        }
    }

    [Fact]
    public void A_missing_file_yields_defaults_and_writes_them_to_disk()
    {
        var path = NewPath();
        try
        {
            Assert.False(File.Exists(path));

            var service = new JsonSettingsService(path);

            Assert.Equal(new AppSettings(), service.Current);
            Assert.True(File.Exists(path)); // defaults are persisted on first run
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task Update_persists_and_reloads_across_instances()
    {
        var path = NewPath();
        try
        {
            var service = new JsonSettingsService(path);
            var updated = service.Current with
            {
                Fps = 30,
                BitrateMbps = 24,
                GameOnlyAudio = false,
                MixMicrophone = true,
            };

            var returned = await service.UpdateAsync(updated);

            Assert.Equal(updated, returned);
            Assert.Equal(updated, service.Current);

            // A fresh instance reading the same file must see the persisted values.
            var reloaded = new JsonSettingsService(path);
            Assert.Equal(30, reloaded.Current.Fps);
            Assert.Equal(24, reloaded.Current.BitrateMbps);
            Assert.False(reloaded.Current.GameOnlyAudio);
            Assert.True(reloaded.Current.MixMicrophone);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void A_corrupt_file_is_preserved_not_overwritten()
    {
        var path = NewPath();
        try
        {
            const string original = "{ \"LibraryDir\": \"D:/my vods\", oops trailing junk ";
            File.WriteAllText(path, original);

            var service = new JsonSettingsService(path);

            // Falls back to defaults in memory, without throwing.
            Assert.Equal(new AppSettings(), service.Current);

            // The unreadable original must NOT be destroyed: it is preserved as a .corrupt sidecar the
            // user can recover from, and a fresh, valid defaults file is written in its place.
            Assert.True(File.Exists(path + ".corrupt"));
            Assert.Equal(original, File.ReadAllText(path + ".corrupt"));
            Assert.True(File.Exists(path));
            Assert.NotNull(new JsonSettingsService(path).Current); // the replacement file is valid
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void An_empty_file_is_treated_as_unreadable_and_preserved()
    {
        var path = NewPath();
        try
        {
            File.WriteAllText(path, string.Empty);

            var service = new JsonSettingsService(path);

            Assert.Equal(new AppSettings(), service.Current);
            Assert.True(File.Exists(path + ".corrupt"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task The_storage_subsection_survives_a_round_trip()
    {
        var path = NewPath();
        try
        {
            var service = new JsonSettingsService(path);
            var updated = service.Current with
            {
                Storage = service.Current.Storage with { HotSetSize = 9, TotalCapBytes = 500L << 30 },
            };

            await service.UpdateAsync(updated);

            var reloaded = new JsonSettingsService(path);
            Assert.Equal(9, reloaded.Current.Storage.HotSetSize);
            Assert.Equal(500L << 30, reloaded.Current.Storage.TotalCapBytes);
        }
        finally
        {
            Cleanup(path);
        }
    }
}
