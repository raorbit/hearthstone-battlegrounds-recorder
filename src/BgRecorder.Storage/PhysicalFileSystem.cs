using System.Security.Cryptography;
using BgRecorder.Core.Storage;

namespace BgRecorder.Storage;

/// <summary>
/// The production <see cref="IFileSystem"/>. Copies flush to disk before returning so a crash right
/// after a copy cannot lose it, and verification uses a SHA-256 content hash (a future optimization
/// could swap in a faster non-cryptographic hash for the multi-GB verify).
/// </summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    private const int BufferSize = 1 << 20;

    public bool FileExists(string path) => File.Exists(path);

    public long GetFileSizeBytes(string path) => new FileInfo(path).Length;

    public void CreateDirectoryForFile(string filePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        CreateDirectoryForFile(destinationPath);
        await using var input = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize,
            FileOptions.Asynchronous);

        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        // Force the OS write-behind cache to physical media so the verified copy survives a crash.
        output.Flush(flushToDisk: true);
    }

    public async Task<string> ComputeContentHashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
