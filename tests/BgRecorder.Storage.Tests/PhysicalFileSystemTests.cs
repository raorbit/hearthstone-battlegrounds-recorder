using BgRecorder.Storage;
using Xunit;

namespace BgRecorder.Storage.Tests;

public sealed class PhysicalFileSystemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bgfs-" + Guid.NewGuid().ToString("N"));

    public PhysicalFileSystemTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Copy_reproduces_the_bytes_and_a_matching_hash_creating_the_target_directory()
    {
        var fs = new PhysicalFileSystem();
        var src = Path.Combine(_dir, "src.bin");
        var dst = Path.Combine(_dir, "nested", "dst.bin"); // directory does not exist yet
        var data = new byte[1_500_000];
        new Random(7).NextBytes(data);
        await File.WriteAllBytesAsync(src, data);

        await fs.CopyAsync(src, dst);

        Assert.True(fs.FileExists(dst));
        Assert.Equal(data.Length, fs.GetFileSizeBytes(dst));
        Assert.Equal(data, await File.ReadAllBytesAsync(dst));
        Assert.Equal(await fs.ComputeContentHashAsync(src), await fs.ComputeContentHashAsync(dst));
    }

    [Fact]
    public async Task The_hash_differs_for_a_single_flipped_byte()
    {
        var fs = new PhysicalFileSystem();
        var a = Path.Combine(_dir, "a.bin");
        var b = Path.Combine(_dir, "b.bin");
        await File.WriteAllBytesAsync(a, [1, 2, 3]);
        await File.WriteAllBytesAsync(b, [1, 2, 4]);

        Assert.NotEqual(await fs.ComputeContentHashAsync(a), await fs.ComputeContentHashAsync(b));
    }

    [Fact]
    public void Delete_is_a_no_op_for_a_missing_file()
    {
        var fs = new PhysicalFileSystem();
        var path = Path.Combine(_dir, "ghost.bin");

        fs.Delete(path); // must not throw

        Assert.False(fs.FileExists(path));
    }
}
