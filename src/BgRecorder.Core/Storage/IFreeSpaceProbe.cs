namespace BgRecorder.Core.Storage;

/// <summary>Free-space source for a volume, injectable so tests don't need a real drive.</summary>
public interface IFreeSpaceProbe
{
    /// <summary>Available free bytes on the volume containing <paramref name="path"/>.</summary>
    long GetAvailableFreeBytes(string path);
}

/// <summary>Production probe backed by <see cref="DriveInfo"/>.</summary>
public sealed class DriveFreeSpaceProbe : IFreeSpaceProbe
{
    public long GetAvailableFreeBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path))
                   ?? throw new ArgumentException($"Cannot resolve a volume root for '{path}'.", nameof(path));
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
