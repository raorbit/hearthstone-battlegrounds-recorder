namespace BgRecorder.Core.Storage;

/// <summary>
/// The file operations the archive mover needs, behind an interface so the mover is unit-testable
/// and fault-injectable (a fake can throw mid-copy, corrupt the destination, or vanish a source).
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);

    /// <summary>Size of an existing file in bytes.</summary>
    long GetFileSizeBytes(string path);

    /// <summary>Ensures the directory that will contain <paramref name="filePath"/> exists.</summary>
    void CreateDirectoryForFile(string filePath);

    /// <summary>Copies the source to the destination, flushing to disk before returning.</summary>
    Task CopyAsync(string sourcePath, string destinationPath, CancellationToken ct = default);

    /// <summary>A content hash used to verify a copy is byte-identical to its source.</summary>
    Task<string> ComputeContentHashAsync(string path, CancellationToken ct = default);

    /// <summary>Deletes a file if it exists; a missing file is not an error.</summary>
    void Delete(string path);
}
